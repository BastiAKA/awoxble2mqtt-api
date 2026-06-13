#if WINDOWS
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace AwoxController.Ble;

/// <summary>
/// WinRT (Windows) counterpart to <see cref="BleAdvStatusService"/>: reads live AwoX lamp status from
/// <b>passive BLE advertisements</b> and feeds it into <see cref="BleLightService"/> — no connection,
/// login or session key, and it never steals the gateway link. The lamps broadcast their full state
/// (company id <c>0x0160</c>) unencrypted; see <see cref="AwoxAdvertStatus"/> for the layout.
///
/// Where the BlueZ service POLLS the adapter cache each tick, WinRT is push-based: a
/// <see cref="BluetoothLEAdvertisementWatcher"/> raises <c>Received</c> per sighting. Those events can
/// fire on multiple threadpool threads, so each is funnelled (cheap parse only) into a bounded channel
/// and drained by a single consumer loop — keeping the same single-threaded contract the shared
/// <see cref="AdvertScanMacResolver"/> (MAC map + MeshId self-heal) was written for. Windows-only;
/// self-disables when the backend is off or status scanning is turned off.
/// </summary>
public sealed class WindowsBleAdvStatusService : BackgroundService
{
    // Pair the BlueZ poll cadence isn't needed here (push), but we still rebuild the MAC map periodically
    // so lamps added to the registry start being matched without a restart.
    private static readonly TimeSpan MapRebuildInterval = TimeSpan.FromSeconds(60);

    // How long to wait before retrying watcher.Start() when the Bluetooth radio is off/unavailable.
    private static readonly TimeSpan RadioRetryInterval = TimeSpan.FromSeconds(15);

    private readonly BleLightService _lights;
    private readonly AwoxBleOptions _options;
    private readonly ILogger<WindowsBleAdvStatusService> _logger;
    private readonly AdvertScanMacResolver _macs;

    public WindowsBleAdvStatusService(
        BleLightService lights, IServiceScopeFactory scopes,
        IOptions<AwoxBleOptions> options, ILogger<WindowsBleAdvStatusService> logger)
    {
        _lights = lights;
        _options = options.Value;
        _logger = logger;
        _macs = new AdvertScanMacResolver(scopes, _options, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.StatusScanEnabled)
        {
            _logger.LogInformation("AwoX advert status scan disabled (AwoxBle:Enabled/StatusScanEnabled).");
            return;
        }
        if (!OperatingSystem.IsWindows())
            return;

        // Received fires on threadpool threads; funnel the cheap (normMac, company-stripped bytes) tuple
        // into a bounded drop-oldest channel so a burst can never block the radio callback, and drain it
        // single-threaded below.
        var channel = Channel.CreateBounded<(string NormMac, byte[] Data)>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        // Active (not Passive): AwoX carries the status manufacturer data in the scan response, which only
        // active scanning solicits — matches BlueZ's active discovery on the Pi and the existing scanner.
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var loggedFirst = 0;
        watcher.Received += (_, args) =>
        {
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != AwoxAdvertStatus.CompanyId)
                    continue;
                // WinRT exposes the manufacturer payload WITHOUT the company id (it's the separate
                // CompanyId field) — exactly the "company-stripped" form AwoxAdvertStatus.TryParse wants.
                if (Interlocked.Exchange(ref loggedFirst, 1) == 0)
                    _logger.LogInformation("First AwoX (0x0160) advert received from {Mac}.", NormalizeAddress(args.BluetoothAddress));
                channel.Writer.TryWrite((NormalizeAddress(args.BluetoothAddress), ToBytes(md.Data)));
                break;
            }
        };
        watcher.Stopped += (_, args) =>
            _logger.LogWarning("AwoX advert watcher stopped (error={Error}); will re-arm.", args.Error);

        var byMac = await _macs.BuildMacMapAsync(stoppingToken);
        var mapBuiltUtc = DateTime.UtcNow;
        var reader = channel.Reader;
        var announced = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // (Re)arm the watcher. Start() throws COMException 0x800710DF when the Bluetooth radio is
                // off/unavailable; that must NOT take down the host (a BackgroundService's unhandled
                // exception defaults to StopHost). Log and retry, so the scan starts itself once Bluetooth
                // is turned on — and re-arms if the watcher later aborts (e.g. the radio is toggled off).
                if (watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    try
                    {
                        watcher.Start();
                        if (!announced)
                        {
                            _logger.LogInformation("AwoX advert status scan active (WinRT) for {Count} lamp(s) — scan-only, no connection held.", byMac.Count);
                            announced = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not start the BLE advert watcher (Bluetooth radio off/unavailable?); retrying in {Seconds}s.", RadioRetryInterval.TotalSeconds);
                        await Task.Delay(RadioRetryInterval, stoppingToken);
                        continue;
                    }
                }

                // Pick up registry changes without a restart, and recover if the DB wasn't ready at start.
                if (byMac.Count == 0 || DateTime.UtcNow - mapBuiltUtc > MapRebuildInterval)
                {
                    byMac = await _macs.BuildMacMapAsync(stoppingToken);
                    mapBuiltUtc = DateTime.UtcNow;
                }

                // Wait for the next sighting, but cap the wait so the map-rebuild + watcher-status checks
                // above still run on an idle mesh. A timed-out wait is normal — just loop.
                (string NormMac, byte[] Data) item;
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(MapRebuildInterval);
                    if (!await reader.WaitToReadAsync(wait.Token) || !reader.TryRead(out item))
                        continue;
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    continue; // rebuild-interval tick, no advert — re-loop
                }

                if (!byMac.TryGetValue(item.NormMac, out var deviceId))
                    continue;
                if (AwoxAdvertStatus.TryParse(AwoxAdvertStatus.CompanyId, item.Data, out var status))
                {
                    // ApplyAdvertStatus updates the live cache AND publishes to IBleAdvertStream, so the
                    // relay-verify confirmation signal works on Windows too.
                    _lights.ApplyAdvertStatus(deviceId, status);
                    await _macs.TryFixMeshIdFromAdvertAsync(item.NormMac, status.MeshId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        finally
        {
            try { watcher.Stop(); } catch { /* already stopped / never started */ }
        }
    }

    private static byte[] ToBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Match AdvertScanMacResolver.NormalizeMac (upper-case hex, no separators). The address is the low 6
    // bytes of the ulong, MAC order is high-to-low.
    private static string NormalizeAddress(ulong address)
    {
        var b = BitConverter.GetBytes(address); // little-endian
        return string.Concat(new[] { b[5], b[4], b[3], b[2], b[1], b[0] }.Select(x => x.ToString("X2")));
    }
}
#endif
