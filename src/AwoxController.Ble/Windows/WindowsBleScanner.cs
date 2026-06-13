#if WINDOWS
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace AwoxController.Ble;

/// <summary>
/// WinRT (Windows) implementation of <see cref="IAwoxBleScanner"/>. Combines two discovery paths so
/// even the newer SIG-mesh AwoX lamps surface: a live advertisement watcher, plus an enumeration of
/// BLE devices Windows already knows (paired/connected) — those may not advertise classically while
/// connected to the AwoX app. Each result is classified (Telink mesh / SIG mesh / unknown).
/// </summary>
public sealed class WindowsBleScanner : IAwoxBleScanner
{
    private readonly ILogger<WindowsBleScanner> _logger;

    public WindowsBleScanner(ILogger<WindowsBleScanner> logger) => _logger = logger;

    public async Task<IReadOnlyList<DiscoveredBleDevice>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var seen = new ConcurrentDictionary<ulong, DiscoveredBleDevice>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        watcher.Received += (_, args) =>
        {
            var name = args.Advertisement.LocalName ?? "";
            var uuids = args.Advertisement.ServiceUuids.Select(g => g.ToString()).ToArray();
            var companyIds = args.Advertisement.ManufacturerData.Select(m => (int)m.CompanyId).Distinct().ToArray();

            seen.AddOrUpdate(args.BluetoothAddress,
                _ => Classify(new DiscoveredBleDevice
                {
                    Address = FormatMac(args.BluetoothAddress),
                    Name = name,
                    Rssi = args.RawSignalStrengthInDBm,
                    ServiceUuids = uuids,
                    ManufacturerCompanyIds = companyIds,
                    Connectable = args.IsConnectable,
                    Source = "advertisement"
                }),
                (_, existing) =>
                {
                    if (!string.IsNullOrEmpty(name)) existing.Name = name;
                    existing.Rssi = args.RawSignalStrengthInDBm;
                    existing.Connectable = args.IsConnectable;
                    if (uuids.Length > 0)
                        existing.ServiceUuids = existing.ServiceUuids.Union(uuids, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (companyIds.Length > 0)
                        existing.ManufacturerCompanyIds = existing.ManufacturerCompanyIds.Union(companyIds).ToArray();
                    return Classify(existing);
                });
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, ct);
        }
        finally
        {
            watcher.Stop();
        }

        await AddKnownDevicesAsync(seen);

        return seen.Values
            .OrderByDescending(d => d.AdvertisesMeshService || d.IsSigMesh || d.LooksLikeAwox)
            .ThenByDescending(d => d.Rssi ?? short.MinValue)
            .ToList();
    }

    public async Task<BleProbeResult> ProbeAsync(string mac, CancellationToken ct = default)
    {
        var result = new BleProbeResult { Address = mac };
        if (!OperatingSystem.IsWindows())
        {
            result.Error = "BLE is only supported through WinRT on Windows.";
            return result;
        }

        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(WindowsBleConnection.ParseMac(mac));
            if (device is null)
            {
                result.Error = "Device not reachable (run a scan first / move it closer).";
                return result;
            }

            var servicesResult = await device.GetGattServicesForUuidAsync(
                new Guid(AwoxBleConstants.MeshServiceUuid), BluetoothCacheMode.Uncached);
            result.Connected = servicesResult.Status == GattCommunicationStatus.Success;
            result.HasMeshService = servicesResult.Status == GattCommunicationStatus.Success && servicesResult.Services.Count > 0;

            if (result.HasMeshService)
            {
                var charResult = await servicesResult.Services[0].GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charResult.Status == GattCommunicationStatus.Success)
                {
                    var ids = charResult.Characteristics.Select(c => c.Uuid).ToHashSet();
                    result.HasPairChar = ids.Contains(new Guid(AwoxMeshProtocol.PairCharUuid));
                    result.HasCommandChar = ids.Contains(new Guid(AwoxMeshProtocol.CommandCharUuid));
                    result.HasStatusChar = ids.Contains(new Guid(AwoxMeshProtocol.StatusCharUuid));
                }
                else
                {
                    result.Error = $"Reading characteristics returned {charResult.Status}.";
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogWarning(ex, "Probe of {Mac} failed.", mac);
        }
        finally
        {
            device?.Dispose();
        }

        return result;
    }

    public async Task<string> DumpGattAsync(string mac, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return "GATT dump is only available on Windows (WinRT).";

        var sb = new System.Text.StringBuilder();
        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(WindowsBleConnection.ParseMac(mac));
            if (device is null) return $"Device {mac} not reachable (scan first / move closer).";

            var svc = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            sb.AppendLine($"GATT dump {mac} — services status={svc.Status}, count={svc.Services.Count}");
            foreach (var s in svc.Services)
            {
                sb.AppendLine($"SERVICE handle=0x{s.AttributeHandle:X4} uuid={s.Uuid}");
                var chr = await s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (chr.Status != GattCommunicationStatus.Success)
                {
                    sb.AppendLine($"  (characteristics: {chr.Status})");
                    continue;
                }
                foreach (var c in chr.Characteristics)
                    sb.AppendLine($"  CHAR handle=0x{c.AttributeHandle:X4} props=[{c.CharacteristicProperties}] uuid={c.Uuid}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR: {ex.Message}");
            _logger.LogWarning(ex, "GATT dump of {Mac} failed.", mac);
        }
        finally
        {
            device?.Dispose();
        }
        var text = sb.ToString();
        _logger.LogInformation("GATT DUMP\n{Dump}", text);
        return text;
    }

    /// <summary>Adds BLE devices Windows already knows (paired or currently connected) to the result set.</summary>
    private async Task AddKnownDevicesAsync(ConcurrentDictionary<ulong, DiscoveredBleDevice> seen)
    {
        var selectors = new (string Selector, string Source)[]
        {
            (BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), "connected"),
            (BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), "paired")
        };

        foreach (var (selector, source) in selectors)
        {
            DeviceInformationCollection infos;
            try { infos = await DeviceInformation.FindAllAsync(selector); }
            catch (Exception ex) { _logger.LogDebug(ex, "Enumerating {Source} devices failed (ignored).", source); continue; }

            foreach (var info in infos)
            {
                BluetoothLEDevice? dev = null;
                try
                {
                    dev = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (dev is null) continue;

                    seen.AddOrUpdate(dev.BluetoothAddress,
                        _ => Classify(new DiscoveredBleDevice
                        {
                            Address = FormatMac(dev.BluetoothAddress),
                            Name = info.Name ?? dev.Name ?? "",
                            Source = source
                        }),
                        (_, existing) =>
                        {
                            if (string.IsNullOrEmpty(existing.Name) && !string.IsNullOrEmpty(info.Name))
                                existing.Name = info.Name;
                            return Classify(existing);
                        });
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Reading known device {Id} failed (ignored).", info.Id); }
                finally { dev?.Dispose(); }
            }
        }
    }

    private static DiscoveredBleDevice Classify(DiscoveredBleDevice d)
    {
        d.AdvertisesMeshService = AwoxBleConstants.AdvertisesMeshService(d.ServiceUuids);
        d.IsSigMesh = AwoxBleConstants.IsSigMesh(d.ServiceUuids);
        var awoxOui = AwoxBleConstants.HasAwoxOui(d.Address);
        var nameMatch = AwoxBleConstants.LooksLikeAwox(d.Name, d.ServiceUuids);
        d.LooksLikeAwox = nameMatch || awoxOui;
        d.Classification = AwoxBleConstants.Classify(d.AdvertisesMeshService, d.IsSigMesh, awoxOui, nameMatch);
        return d;
    }

    private static string FormatMac(ulong address)
    {
        var bytes = BitConverter.GetBytes(address); // little-endian; MAC is the low 6 bytes, high-to-low
        return string.Join(":", new[] { bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0] }
            .Select(b => b.ToString("X2")));
    }
}
#endif
