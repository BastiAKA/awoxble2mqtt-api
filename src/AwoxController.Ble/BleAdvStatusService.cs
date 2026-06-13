using AwoxController.Core.Interfaces;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwoxController.Ble;

/// <summary>
/// Reads live Connect-Z lamp status from <b>passive BLE advertisements</b> (BlueZ/Linux) and feeds it
/// into <see cref="BleLightService"/>. The lamps broadcast their full state (power/brightness/colour/
/// white-temp) unencrypted in their manufacturer data, so this needs NO connection, login or session
/// key — and crucially never steals the gateway link from the AwoX app or hub. See
/// <see cref="AwoxAdvertStatus"/> for the decoded layout.
///
/// Linux/BlueZ only: on Windows the WinRT connection path supplies status, so this self-disables. It
/// also self-disables when the backend is off or advert status scanning is turned off. The scan runs
/// continuously; each tick it reads the freshest advert BlueZ cached per configured bulb (matched by
/// MAC) and applies it. Poll cadence is the DB-tunable <c>ble.poll_interval_seconds</c>.
/// </summary>
public sealed class BleAdvStatusService : BackgroundService
{
    private readonly BleLightService _lights;
    private readonly IAwoxBleConnection _connection;
    private readonly IBleAdvertStream _adverts;
    private readonly AwoxBleOptions _options;
    private readonly IAppSettings _settings;
    private readonly ILogger<BleAdvStatusService> _logger;
    private readonly AdvertScanMacResolver _macs;

    public BleAdvStatusService(
        BleLightService lights, IAwoxBleConnection connection, IBleAdvertStream adverts,
        IServiceScopeFactory scopes, IOptions<AwoxBleOptions> options, IAppSettings settings,
        ILogger<BleAdvStatusService> logger)
    {
        _lights = lights;
        _connection = connection;
        _adverts = adverts;
        _options = options.Value;
        _settings = settings;
        _logger = logger;
        _macs = new AdvertScanMacResolver(scopes, _options, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.StatusScanEnabled)
        {
            _logger.LogInformation("Connect-Z advert status scan disabled (AwoxBle:Enabled/StatusScanEnabled).");
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogInformation("Connect-Z advert status scan runs on Linux/BlueZ only; on Windows status comes from the WinRT connection.");
            return;
        }

        // Scan for EVERY lamp in the device registry, matched by MAC — the registry is the source of
        // truth, no parallel hand-maintained list. Value = the lamp's canonical MAC, which we pass on as
        // the device id so the state cache is keyed by MAC (the same key /api/devices overlays on).
        // Rebuilt periodically so lamps added later start being scanned without a restart. Falls back to
        // the AwoxBle:Devices config when no registry/DB is configured.
        var byMac = await _macs.BuildMacMapAsync(stoppingToken);
        var mapBuiltUtc = DateTime.UtcNow;
        _logger.LogInformation("AwoX advert status scan active for {Count} lamp(s) — passive, no connection held.", byMac.Count);

        // The USB BT dongle re-enumerates on the Pi (hci0 ⇄ hci1), which invalidates any cached adapter
        // reference and clears BlueZ's device cache. So resolve the adapter and (re)start discovery on
        // EVERY tick rather than holding one reference — that survives the index churn transparently.
        var loggedAdapterMissing = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Pick up registry changes (newly-added lamps) without a restart, and recover if the DB
                // wasn't ready at startup. Cheap read, no writes — fine on the SD card.
                if (byMac.Count == 0 || DateTime.UtcNow - mapBuiltUtc > TimeSpan.FromSeconds(60))
                {
                    byMac = await _macs.BuildMacMapAsync(stoppingToken);
                    mapBuiltUtc = DateTime.UtcNow;
                }

                try
                {
                    // Yield the radio ONLY during a connect handshake: restarting discovery mid-connect
                    // aborts it (le-connection-abort-by-local). A fully-held connection is fine to scan
                    // alongside — the BT500 does both concurrently (proven on hardware), so we keep
                    // scanning while a connection is held and get live status of every lamp during control.
                    if (_connection.IsConnecting)
                    {
                        // nothing this tick — a connect handshake owns the adapter
                    }
                    else
                    {
                        // GetAdaptersAsync returns a FRESH Adapter proxy every call, and each holds a
                        // D-Bus PropertiesChanged watcher (Adapter : IDisposable). Dispose it at the end
                        // of every tick — otherwise the 5s poll leaks one watcher per tick forever
                        // (the OOM root cause). `using` on null is a no-op.
                        using var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
                        if (adapter is not null)
                        {
                            loggedAdapterMissing = false;
                            await EnsureDiscoveringAsync(adapter);
                            await PollOnceAsync(adapter, byMac);
                        }
                        else if (!loggedAdapterMissing)
                        {
                            _logger.LogWarning("Connect-Z advert scan: no Bluetooth adapter present; will keep retrying.");
                            loggedAdapterMissing = true;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Connect-Z advert scan tick failed (adapter churn?); retrying next tick.");
                }

                // While a confirmation is being awaited (a relay-verify subscribed to the advert stream),
                // poll FAST — the lamp emits its change advert immediately, and at the normal multi-second
                // cadence we'd read BlueZ's cache too late and miss it inside the verify window. Idle, stay
                // on the economical poll interval so the dongle isn't hammered.
                TimeSpan delay;
                if (_adverts.HasSubscribers)
                {
                    var fastMs = Math.Clamp(
                        _settings.GetInt(AppSettingKeys.BleAdvertFastPollMs, AppSettingKeys.BleAdvertFastPollMsDefault),
                        100, 2000);
                    delay = TimeSpan.FromMilliseconds(fastMs);
                }
                else
                {
                    var interval = Math.Clamp(
                        _settings.GetInt(AppSettingKeys.BlePollIntervalSeconds, AppSettingKeys.BlePollIntervalSecondsDefault),
                        2, 60);
                    delay = TimeSpan.FromSeconds(interval);
                }
                await Task.Delay(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    // (Re)start LE discovery on the freshly-resolved adapter when it isn't already discovering — also
    // covers a command connection (BlueZBleConnection) having stopped discovery in between.
    private async Task EnsureDiscoveringAsync(Adapter adapter)
    {
        bool discovering;
        try { discovering = await adapter.GetDiscoveringAsync(); }
        catch { discovering = false; }
        if (discovering) return;

        try { await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object> { ["Transport"] = "le" }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not set LE discovery filter (ignored)."); }
        try
        {
            await adapter.StartDiscoveryAsync();
            _logger.LogInformation("Connect-Z advert scan: (re)started LE discovery on the current adapter.");
        }
        catch (Exception ex) { _logger.LogDebug(ex, "StartDiscovery threw (ignored; retrying next tick)."); }
    }

    private async Task PollOnceAsync(Adapter adapter, IReadOnlyDictionary<string, string> byMac)
    {
        IReadOnlyList<Device> devices;
        try { devices = await adapter.GetDevicesAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "GetDevices threw (ignored this tick)."); return; }

        foreach (var device in devices)
        {
            // Each Device proxy from GetDevicesAsync is fresh and IDisposable (holds a D-Bus
            // PropertiesChanged watcher). Dispose it at the end of THIS iteration — with ~26 devices
            // every 5s, not disposing leaks ~5 watchers/second until the process is OOM-killed.
            using var _owned = device;

            string? address;
            try { address = await device.GetAddressAsync(); }
            catch { continue; }
            if (address is null || !byMac.TryGetValue(AdvertScanMacResolver.NormalizeMac(address), out var deviceId))
                continue;

            IDictionary<ushort, object>? manufacturerData;
            try { manufacturerData = await device.GetManufacturerDataAsync(); }
            catch { continue; }
            if (manufacturerData is null
                || !manufacturerData.TryGetValue(AwoxAdvertStatus.CompanyId, out var raw)
                || raw is not byte[] bytes)
                continue;

            if (AwoxAdvertStatus.TryParse(AwoxAdvertStatus.CompanyId, bytes, out var status))
            {
                _lights.ApplyAdvertStatus(deviceId, status);
                await _macs.TryFixMeshIdFromAdvertAsync(AdvertScanMacResolver.NormalizeMac(address), status.MeshId);
            }
        }
    }
}
