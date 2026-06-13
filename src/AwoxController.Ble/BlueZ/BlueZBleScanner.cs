using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Microsoft.Extensions.Logging;

namespace AwoxController.Ble;

/// <summary>
/// BlueZ (Linux) implementation of <see cref="IAwoxBleScanner"/>. Only functional on Linux;
/// elsewhere the methods return empty results (use the WinRT scanner on Windows).
/// </summary>
public sealed class BlueZBleScanner : IAwoxBleScanner
{
    private readonly ILogger<BlueZBleScanner> _logger;

    public BlueZBleScanner(ILogger<BlueZBleScanner> logger) => _logger = logger;

    public async Task<IReadOnlyList<DiscoveredBleDevice>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("BlueZ BLE scan only runs on Linux. On Windows, launch the " +
                               "net10.0-windows10.0.19041.0 target to use the WinRT scanner.");
            return [];
        }

        using var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
        if (adapter is null)
        {
            _logger.LogError("No Bluetooth adapter found.");
            return [];
        }

        // Restrict discovery to LE for speed; ignore if the adapter rejects the filter.
        try { await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object> { ["Transport"] = "le" }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not set LE discovery filter (ignored)."); }

        await adapter.StartDiscoveryAsync();
        try
        {
            await Task.Delay(duration, ct);
        }
        finally
        {
            try { await adapter.StopDiscoveryAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "StopDiscovery threw (ignored)."); }
        }

        var devices = await adapter.GetDevicesAsync();
        var results = new List<DiscoveredBleDevice>(devices.Count);

        foreach (var device in devices)
        {
            using var _owned = device; // fresh IDisposable proxy per device — dispose each, don't leak its watcher

            var address = await TryGetAsync(() => device.GetAddressAsync());
            if (address is null) continue;

            var name = await TryGetAsync(() => device.GetNameAsync())
                       ?? await TryGetAsync(() => device.GetAliasAsync());
            var rssi = await TryGetAsync(() => device.GetRSSIAsync());
            var uuids = await TryGetAsync(() => device.GetUUIDsAsync()) ?? [];
            var manufacturerData = await TryGetAsync(() => device.GetManufacturerDataAsync());
            var serviceData = await TryGetAsync(() => device.GetServiceDataAsync());

            // Service-data keys are also service UUIDs (SIG mesh proxy/provisioning often advertise here).
            var allUuids = serviceData is null ? uuids : uuids.Union(serviceData.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
            var companyIds = manufacturerData is null ? [] : manufacturerData.Keys.Select(k => (int)k).ToArray();

            var telink = AwoxBleConstants.AdvertisesMeshService(allUuids);
            var sigMesh = AwoxBleConstants.IsSigMesh(allUuids);
            var awoxOui = AwoxBleConstants.HasAwoxOui(address);
            var nameMatch = AwoxBleConstants.LooksLikeAwox(name, allUuids);

            results.Add(new DiscoveredBleDevice
            {
                Address = address,
                Name = name ?? "",
                Rssi = rssi,
                ServiceUuids = allUuids,
                ManufacturerCompanyIds = companyIds,
                AdvertisesMeshService = telink,
                IsSigMesh = sigMesh,
                LooksLikeAwox = nameMatch || awoxOui,
                Classification = AwoxBleConstants.Classify(telink, sigMesh, awoxOui, nameMatch)
            });
        }

        return results
            .OrderByDescending(d => d.AdvertisesMeshService || d.IsSigMesh || d.LooksLikeAwox)
            .ThenByDescending(d => d.Rssi ?? short.MinValue)
            .ToList();
    }

    public async Task<string> DumpGattAsync(string mac, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux())
            return "BlueZ GATT dump only runs on Linux.";

        var sb = new System.Text.StringBuilder();
        try
        {
            using var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
            using var device = adapter is null ? null : await adapter.GetDeviceAsync(mac);
            if (device is null) return $"Device {mac} not found (scan first).";
            try { await device.ConnectAsync(); await device.WaitForPropertyValueAsync("ServicesResolved", true, TimeSpan.FromSeconds(20)); } catch { }
            foreach (var s in await device.GetServicesAsync())
            {
                sb.AppendLine($"SERVICE uuid={await s.GetUUIDAsync()}");
                foreach (var c in await s.GetCharacteristicsAsync())
                    sb.AppendLine($"  CHAR uuid={await c.GetUUIDAsync()} path={c.ObjectPath}");
            }
        }
        catch (Exception ex) { sb.AppendLine($"ERROR: {ex.Message}"); }
        return sb.ToString();
    }

    public async Task<BleProbeResult> ProbeAsync(string mac, CancellationToken ct = default)
    {
        var result = new BleProbeResult { Address = mac };
        if (!OperatingSystem.IsLinux())
        {
            result.Error = "BlueZ BLE only runs on Linux. On Windows, launch the net10.0-windows target.";
            return result;
        }

        using var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
        if (adapter is null)
        {
            result.Error = "No Bluetooth adapter found.";
            return result;
        }

        Device? device = null;
        try
        {
            device = await adapter.GetDeviceAsync(mac);
            if (device is null)
            {
                result.Error = "Device not known to BlueZ (run a scan first / move it closer).";
                return result;
            }

            await device.ConnectAsync();
            await device.WaitForPropertyValueAsync("Connected", true, TimeSpan.FromSeconds(15));
            await device.WaitForPropertyValueAsync("ServicesResolved", true, TimeSpan.FromSeconds(15));
            result.Connected = true;

            result.ServiceUuids = await TryGetAsync(() => device.GetUUIDsAsync()) ?? [];

            var service = await device.GetServiceAsync(AwoxBleConstants.MeshServiceUuid);
            result.HasMeshService = service is not null;
            if (service is not null)
            {
                result.HasPairChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.PairCharUuid) is not null;
                result.HasCommandChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.CommandCharUuid) is not null;
                result.HasStatusChar = await service.GetCharacteristicAsync(AwoxMeshProtocol.StatusCharUuid) is not null;
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogWarning(ex, "Probe of {Mac} failed.", mac);
        }
        finally
        {
            if (device is not null)
            {
                try { await device.DisconnectAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Disconnect after probe threw (ignored)."); }
                device.Dispose();
            }
        }

        return result;
    }

    private async Task<T?> TryGetAsync<T>(Func<Task<T>> getter)
    {
        try { return await getter(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading a device property failed (ignored).");
            return default;
        }
    }
}
