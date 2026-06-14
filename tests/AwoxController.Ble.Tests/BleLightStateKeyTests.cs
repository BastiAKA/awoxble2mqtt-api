using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AwoxController.Ble.Tests;

/// <summary>
/// Regression cover for the "dimmer jumps back" bug: the command path addresses a lamp by its friendly
/// <c>Name</c> while the passive advert path addresses it by <c>MAC</c>. Those must resolve to ONE live
/// <see cref="LightState"/> — otherwise a command (which doesn't touch brightness) pushes the stale
/// command-side brightness over the value the advert just read, and the UI reverts even though the lamp
/// never changed. Verified affecting both Connect-C and Connect-Z.
/// </summary>
public class BleLightStateKeyTests
{
    private const string Name = "wohnzimmer";
    private const string Mac = "A4:C1:38:20:29:91";

    private static BleLightService Build(out CapturingNotifier notifier)
    {
        var opts = Options.Create(new AwoxBleOptions
        {
            Enabled = true,
            Devices = { new AwoxBleDevice { Name = Name, Mac = Mac, MeshId = 0x52CE } }
        });
        notifier = new CapturingNotifier();
        var svc = new BleLightService(opts, new NoopConnection(), NullLogger<BleLightService>.Instance, notifier);
        svc.StartAsync(default).GetAwaiter().GetResult();
        return svc;
    }

    [Fact]
    public async Task Command_DoesNotRevert_BrightnessReadFromAdvert()
    {
        var svc = Build(out var notifier);

        // 1) Remote dims the lamp; the advert path (keyed by MAC) reads it as 50 %.
        var advert = new AwoxAdvertStatus(
            MeshId: 0x52CE, IsOn: true, IsColorMode: false, Brightness: 0x7F, WhiteTemp: 0x40, Hue: 0, Sat: 0);
        svc.ApplyAdvertStatus(Mac, advert);
        Assert.Equal(50, advert.BrightnessPercent); // 0x7F / 0xFE ≈ 50 %

        // 2) User sends a colour command in the app (addressed by Name) — it must NOT touch brightness.
        await svc.SetColorAsync(Name, new RgbColor(0xFF, 0, 0));

        // 3) Both the read-back and the last pushed state keep the advert's 50 %.
        Assert.True(svc.TryGetState(Name, out var byName));
        Assert.Equal(50, byName.BrightnessPercent);
        Assert.True(svc.TryGetState(Mac, out var byMac));
        Assert.Same(byName, byMac); // one shared state object, not two
        Assert.Equal(50, notifier.Last!.BrightnessPercent);
    }

    private sealed class CapturingNotifier : ILightStateNotifier
    {
        public LightState? Last { get; private set; }
        public void NotifyStateChanged(string deviceId, LightState state) => Last = state;
    }

    private sealed class NoopConnection : IAwoxBleConnection
    {
        public bool IsConnected => false;
        public bool IsConnecting => false;
        public string? ConnectedGatewayMac => null;
        public bool IsConnectedToMesh(string meshName, string meshPassword) => false;
        public string? ConnectedGatewayMacOnMesh(string meshName, string meshPassword) => null;
        public DateTimeOffset? LastActivityUtc => null;
        public event Action<byte[]>? StatusReceived { add { } remove { } }
        public Task<bool> EnsureConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandAsync(ushort destId, byte command, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendZigbeeCommandToAsync(string gatewayMac, ushort destId, byte[] command, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte command, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendZigbeeCommandToAsync(string gatewayMac, string meshName, string meshPassword, ushort destId, byte[] command, CancellationToken ct = default) => Task.CompletedTask;
        public Task<byte[]?> ReadStatusAsync(string gatewayMac, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task SendCommandToAsync(string gatewayMac, ushort destId, byte command, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AwoxLoginTestResult> TryLoginAsync(string mac, string meshName, string meshPassword, CancellationToken ct = default)
            => Task.FromResult(AwoxLoginTestResult.Failed(meshName, meshPassword, "noop"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
