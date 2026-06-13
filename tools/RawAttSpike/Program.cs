using AwoxController.Ble.Raw;

// Raw-ATT status spike: connects to an AwoX Connect-Z bulb over a raw L2CAP ATT channel (no BlueZ
// GATT, no CCCD), logs in, and either listens for status pushes or runs a controlled colour map.
//
// Usage:  RawAttSpike <mac> <meshName> <meshPassword> [seconds]
//         RawAttSpike colormap <mac> <meshName> <meshPassword> <destIdHex>
// Linux only. The bulb must NOT be connected by bluetoothd at the same time.

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

try
{
    if (args.Length >= 5 && args[0].Equals("colormap", StringComparison.OrdinalIgnoreCase))
    {
        var destId = (ushort)Convert.ToInt32(args[4], 16);
        AwoxRawAttSession.ColorMap(args[1], args[2], args[3], destId, Log);
        return 0;
    }

    if (args.Length >= 5 && args[0].Equals("colorquery", StringComparison.OrdinalIgnoreCase))
    {
        var meshId = (ushort)Convert.ToInt32(args[4], 16);
        AwoxRawAttSession.ColorQuery(args[1], args[2], args[3], meshId, Log);
        return 0;
    }

    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: RawAttSpike <mac> <meshName> <meshPassword> [seconds]");
        Console.Error.WriteLine("       RawAttSpike colormap <mac> <meshName> <meshPassword> <destIdHex>");
        return 2;
    }

    var seconds = args.Length > 3 && int.TryParse(args[3], out var s) ? s : 20;
    var frames = AwoxRawAttSession.Probe(args[0], args[1], args[2], seconds, Log);
    Log($"RESULT: {frames} status frame(s).");
    return frames > 0 ? 0 : 1;
}
catch (Exception ex)
{
    Log($"FAILED: {ex.Message}");
    return 3;
}
