using System.Runtime.InteropServices;

namespace AwoxController.Ble.Raw;

/// <summary>
/// A raw ATT channel to one BLE device over a Linux L2CAP socket (fixed CID 0x0004), bypassing the
/// BlueZ GATT client entirely. This is the ONLY way to speak ATT without BlueZ writing the CCCD
/// descriptor — which the AwoX "Connect-Z" bulbs reject (ATT 0x0e) by dropping the link. We send the
/// AwoX login + status-poll PDUs ourselves and receive the bulb's unsolicited Handle Value
/// Notifications directly, never touching the CCCD.
///
/// Linux-only (P/Invoke into libc + the kernel Bluetooth L2CAP stack). On other platforms the ctor
/// throws <see cref="PlatformNotSupportedException"/>.
/// </summary>
public sealed class RawAttSocket : IDisposable
{
    private const int AF_BLUETOOTH = 31;
    private const int SOCK_SEQPACKET = 5;
    private const int BTPROTO_L2CAP = 0;
    private const ushort ATT_CID = 0x0004;
    private const int SOL_SOCKET = 1;
    private const int SO_RCVTIMEO = 20;

    /// <summary>Public device address (e.g. a Telink/AwoX MAC). The default for these bulbs.</summary>
    public const byte AddrTypePublic = 0x01;
    public const byte AddrTypeRandom = 0x02;

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int bind(int fd, byte[] addr, int addrlen);
    [DllImport("libc", SetLastError = true)] private static extern int connect(int fd, byte[] addr, int addrlen);
    [DllImport("libc", SetLastError = true)] private static extern nint send(int fd, byte[] buf, nint len, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint recv(int fd, byte[] buf, nint len, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int setsockopt(int fd, int level, int optname, byte[] optval, int optlen);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    private int _fd = -1;

    /// <summary>
    /// Opens the L2CAP ATT channel to <paramref name="mac"/> (initiating the LE connection). The device
    /// must NOT be ACL-connected by bluetoothd at the same time (ATT is a single channel per link) —
    /// disconnect it from BlueZ first. Throws with the libc errno on failure.
    /// </summary>
    public void Connect(string mac, byte addrType = AddrTypePublic, int recvTimeoutMs = 4000)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("RawAttSocket requires Linux (L2CAP sockets).");

        _fd = socket(AF_BLUETOOTH, SOCK_SEQPACKET, BTPROTO_L2CAP);
        if (_fd < 0) throw Err("socket()");

        // bind to the local adapter (BDADDR_ANY) on the ATT CID.
        if (bind(_fd, BuildSockAddr(new byte[6], ATT_CID, 0), 14) < 0)
        {
            var e = Err("bind()");
            Dispose();
            throw e;
        }

        // receive timeout so the read loop can drain notifications then send the next poll.
        SetRecvTimeout(recvTimeoutMs);

        if (connect(_fd, BuildSockAddr(ParseMacReversed(mac), ATT_CID, addrType), 14) < 0)
        {
            var e = Err($"connect({mac})");
            Dispose();
            throw e;
        }
    }

    /// <summary>Sends one raw ATT PDU.</summary>
    public void Send(byte[] pdu)
    {
        var n = send(_fd, pdu, pdu.Length, 0);
        if (n < 0) throw Err("send()");
    }

    /// <summary>
    /// Receives one ATT PDU into <paramref name="buffer"/>. Returns the byte count, or 0 on receive
    /// timeout (EAGAIN/EWOULDBLOCK) so callers can loop. Throws on a real error.
    /// </summary>
    public int Receive(byte[] buffer)
    {
        var n = recv(_fd, buffer, buffer.Length, 0);
        if (n >= 0) return (int)n;
        var errno = Marshal.GetLastPInvokeError();
        if (errno is 11 or 35) return 0; // EAGAIN / EWOULDBLOCK -> timeout, no data
        throw new IOException($"recv() failed (errno {errno})");
    }

    private void SetRecvTimeout(int ms)
    {
        // struct timeval { long tv_sec; long tv_usec; } — 16 bytes on 64-bit Linux.
        var tv = new byte[16];
        BitConverter.GetBytes((long)(ms / 1000)).CopyTo(tv, 0);
        BitConverter.GetBytes((long)(ms % 1000) * 1000).CopyTo(tv, 8);
        setsockopt(_fd, SOL_SOCKET, SO_RCVTIMEO, tv, tv.Length);
    }

    private static byte[] BuildSockAddr(byte[] bdaddr6, ushort cid, byte addrType)
    {
        // struct sockaddr_l2 { u16 family; u16 psm; bdaddr_t bdaddr(6); u16 cid; u8 bdaddr_type; } + pad
        var a = new byte[14];
        a[0] = unchecked((byte)AF_BLUETOOTH);
        a[1] = (byte)(AF_BLUETOOTH >> 8);
        // psm = 0 (bytes 2..4)
        Array.Copy(bdaddr6, 0, a, 4, 6);
        a[10] = (byte)(cid & 0xff);
        a[11] = (byte)(cid >> 8);
        a[12] = addrType;
        return a;
    }

    /// <summary>"aa:bb:cc:dd:ee:ff" -> bytes in little-endian (reversed) order for sockaddr_l2.</summary>
    private static byte[] ParseMacReversed(string mac)
    {
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) throw new ArgumentException($"Invalid MAC '{mac}'.");
        var b = new byte[6];
        for (var i = 0; i < 6; i++)
            b[5 - i] = Convert.ToByte(parts[i], 16);
        return b;
    }

    private static IOException Err(string what)
        => new($"{what} failed (errno {Marshal.GetLastPInvokeError()})");

    public void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }
}
