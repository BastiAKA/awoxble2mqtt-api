using System.Collections.Concurrent;
using AwoxController.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AwoxController.Ble;

/// <summary>The five independently-controllable aspects of a lamp. One pending command per lamp ×
/// channel: a newer command on the same channel OVERWRITES the older one (coalescing), so a slider
/// drag collapses to a single write while "set colour then dim" keeps both.</summary>
public enum BleChannel
{
    Power = 0,
    Color = 1,
    ColorTemp = 2,
    Brightness = 3,
    ColorBrightness = 4,
}

/// <summary>
/// One queued BLE write. <see cref="Send"/> performs the actual GATT write against the shared
/// connection (built by the caller so the queue stays protocol-agnostic — tlmesh vs zigbee). It takes
/// the gateway MAC to route through as a PARAMETER, so the relay coordinator can send the same write
/// either through an already-held node (relay) or directly to the target. The caller MUST NOT capture
/// the request's CancellationToken in <see cref="Send"/>: the HTTP request completes the moment we
/// return 202, long before the worker drains it; the worker passes its own lifetime token instead.
///
/// <see cref="TargetMac"/> is the lamp the command is FOR (the relay coordinator keys its learned
/// reachability map and its advert verification on it). <see cref="ExpectedState"/>, when non-null, is
/// the predicate the target's advert must satisfy for a relay to count as confirmed; null disables
/// relay verification for this command (e.g. an explicit <c>?via</c> pin), so it takes the direct path.
///
/// <see cref="TargetWasInExpectedState"/> is captured by the CALLER from the target's last observed state
/// BEFORE this command's own optimistic state write — so the coordinator can tell a genuine,
/// relay-attributable change (learn reachability from it) from a no-op confirmation (the advert would
/// confirm regardless). It must be read before the control path seeds the cache with the commanded value,
/// otherwise the target always "already satisfies" its own command and verification is short-circuited.
/// </summary>
public sealed record BleCommand(
    string GatewayMac,
    string TargetMac,
    string MeshName,
    string MeshPassword,
    ushort Dest,
    BleChannel Channel,
    Func<IAwoxBleConnection, string, CancellationToken, Task> Send,
    Func<LightState, bool>? ExpectedState = null,
    bool PowerOff = false,
    bool TargetWasInExpectedState = false,
    long Seq = 0,
    int Attempt = 0);

/// <summary>
/// Accepts lamp commands and drains them on a single background worker. Lets the controllers return
/// 202 immediately instead of blocking on a slow BLE connect/write.
/// </summary>
public interface IBleCommandQueue
{
    /// <summary>Queues a command, coalescing it with any pending command on the same lamp × channel.</summary>
    void Enqueue(BleCommand command);
}

/// <summary>
/// Sink for a relay-verify that couldn't confirm a relayed command: it asks the queue to re-send the
/// command directly. The queue drops the retry when a NEWER command for the same lamp has since been
/// enqueued (the newer intent wins — re-sending a stale colour/brightness would fight it), or when the
/// command has already exhausted its retry budget. So rapid changes stay ms-fast and never re-apply an
/// old state.
/// </summary>
public interface IBleCommandRetrySink
{
    /// <summary>Re-enqueue <paramref name="cmd"/> for one more (now direct) attempt, unless superseded
    /// by a newer command for the same lamp or out of attempts.</summary>
    void RequeueDirect(BleCommand cmd);
}

/// <summary>
/// Serialises BLE control through one background worker, coalescing per (lamp × channel) so a burst
/// of commands collapses to the minimum number of writes. The big win is connection-churn: the worker
/// drains a whole burst over (typically) a single held gateway link instead of each controller call
/// connecting/disconnecting on its own — which directly reduces the dongle thrash that wedges the
/// adapter, and lets the passive advert scan resume sooner.
///
/// Coalescing rule: a newer command on a channel overwrites the pending one (so dragging a slider
/// sends ONE brightness write, not dozens), but different channels are kept (so "colour then dim"
/// keeps both). A power-OFF additionally discards that lamp's pending dim/colour — those would be
/// wasted writes to a lamp that's going dark, and could even re-light it.
///
/// Drain order: lamps oldest-received-first; within a lamp, Power → Colour|Temp → Brightness, so the
/// mode-setting command lands before its brightness (the colour/white mode decides the brightness
/// channel) and a turn-on precedes the rest. Consecutive lamps that share a gateway reuse the held
/// connection automatically (the connection re-logs-in only when the gateway/creds change).
/// </summary>
public sealed class BleCommandQueue : BackgroundService, IBleCommandQueue, IBleCommandRetrySink
{
    // Within one lamp, send the mode-defining commands before brightness, and power before everything.
    private static readonly BleChannel[] SendOrder =
        { BleChannel.Power, BleChannel.Color, BleChannel.ColorTemp, BleChannel.Brightness, BleChannel.ColorBrightness };

    /// <summary>How many times a command may be re-sent after an unconfirmed relay. 1 = one direct retry,
    /// then give up (the lamp got the writes; it just doesn't advert-confirm — don't churn forever).</summary>
    private const int MaxAttempts = 1;

    private readonly IRelayCoordinator _relay;
    private readonly ILogger<BleCommandQueue> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, LampPending> _pending = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();          // lamp keys, oldest-received first
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue); // wakes the worker when work arrives

    // Monotonic command sequence + the newest seq seen per lamp (gateway|dest). A backgrounded relay-verify
    // uses these to drop a stale direct-retry: if a newer command for the lamp arrived after this one, the
    // retry would re-apply an old state, so it's skipped. Never decremented; guarded by _lock.
    private long _seq;
    private readonly Dictionary<string, long> _latestSeqByLamp = new(StringComparer.Ordinal);

    public BleCommandQueue(IRelayCoordinator relay, ILogger<BleCommandQueue> logger)
    {
        _relay = relay;
        _logger = logger;
    }

    public void Enqueue(BleCommand command)
    {
        // (gateway, dest) uniquely identifies the target lamp: dest is its mesh id, the gateway is the
        // bulb we relay through. Two distinct lamps never share both.
        var key = $"{command.GatewayMac}|{command.Dest}";

        lock (_lock)
        {
            // Stamp a sequence and record it as this lamp's newest, so a later retry can tell whether it's
            // been superseded. Done under the lock so the seq and the per-lamp newest stay consistent.
            command = command with { Seq = ++_seq };
            _latestSeqByLamp[key] = command.Seq;

            if (!_pending.TryGetValue(key, out var lamp))
            {
                lamp = new LampPending(command.GatewayMac, command.MeshName, command.MeshPassword, command.Dest);
                _pending[key] = lamp;
                _order.AddLast(key);
            }

            // Power-OFF cancels this lamp's pending light changes — don't fight the off with a re-light.
            if (command.Channel == BleChannel.Power && command.PowerOff)
            {
                lamp.Channels.Remove(BleChannel.Brightness);
                lamp.Channels.Remove(BleChannel.ColorBrightness);
                lamp.Channels.Remove(BleChannel.Color);
                lamp.Channels.Remove(BleChannel.ColorTemp);
            }

            lamp.Channels[command.Channel] = command; // coalesce: newest wins on this channel
        }

        _signal.Release();
    }

    /// <summary>
    /// A backgrounded relay-verify couldn't confirm <paramref name="cmd"/> — re-send it directly, UNLESS a
    /// newer command for the same lamp has since been enqueued (stale: the newer intent wins) or it's out
    /// of attempts. This is what keeps rapid changes fast: only the latest command for a lamp can retry.
    /// </summary>
    public void RequeueDirect(BleCommand cmd)
    {
        var key = $"{cmd.GatewayMac}|{cmd.Dest}";
        lock (_lock)
        {
            if (_latestSeqByLamp.TryGetValue(key, out var latest) && latest != cmd.Seq)
            {
                _logger.LogDebug("Drop retry for {Target} ({Channel}) seq {Seq}: superseded by a newer command (latest {Latest}).",
                    cmd.TargetMac, cmd.Channel, cmd.Seq, latest);
                return;
            }
        }
        if (cmd.Attempt >= MaxAttempts)
        {
            _logger.LogDebug("Drop retry for {Target} ({Channel}): out of attempts.", cmd.TargetMac, cmd.Channel);
            return;
        }
        _logger.LogInformation("Re-queuing {Target} ({Channel}) as a direct retry (attempt {Next}).",
            cmd.TargetMac, cmd.Channel, cmd.Attempt + 1);
        Enqueue(cmd with { Attempt = cmd.Attempt + 1 });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BLE command queue worker started.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);

                // Drain everything currently queued in one pass (new arrivals just re-signal us).
                while (TryDequeueLamp(out var lamp))
                    await DrainLampAsync(lamp, stoppingToken);

                // Collapse any leftover permits from commands we already drained above, so we block
                // again instead of spinning through no-op wakeups.
                while (_signal.Wait(0)) { }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        _logger.LogInformation("BLE command queue worker stopped.");
    }

    private bool TryDequeueLamp(out LampPending lamp)
    {
        lock (_lock)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;          // capture before we mutate the list
                _order.Remove(node);
                if (_pending.Remove(node.Value, out var p) && p.Channels.Count > 0)
                {
                    lamp = p;
                    return true;
                }
                // Empty/stale entry (already gone, or coalesced down to nothing) — keep scanning.
                node = next;
            }
        }

        lamp = null!;
        return false;
    }

    private async Task DrainLampAsync(LampPending lamp, CancellationToken ct)
    {
        foreach (var channel in SendOrder)
        {
            if (!lamp.Channels.TryGetValue(channel, out var cmd)) continue;

            try
            {
                // The relay coordinator sends the command (we await only the WRITE, not the confirmation)
                // and verifies it against the lamp's advert in the BACKGROUND — so the next command isn't
                // held up by the verify. An unconfirmed relay asks us (the retry sink) to re-send directly.
                await _relay.ExecuteAsync(cmd, this, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed write must not drop the lamp's remaining channels or wedge the worker —
                // the connection layer already retries-once on a dropped link; beyond that, log and move on.
                _logger.LogWarning(ex, "Queued BLE {Channel} command to {Gw} dest 0x{Dest:X4} failed.",
                    channel, lamp.GatewayMac, lamp.Dest);
            }
        }
    }

    public override void Dispose()
    {
        _signal.Dispose();
        base.Dispose();
    }

    /// <summary>The coalesced pending commands for one lamp (at most one per channel).</summary>
    private sealed class LampPending(string gatewayMac, string meshName, string meshPassword, ushort dest)
    {
        public string GatewayMac { get; } = gatewayMac;
        public string MeshName { get; } = meshName;
        public string MeshPassword { get; } = meshPassword;
        public ushort Dest { get; } = dest;
        public Dictionary<BleChannel, BleCommand> Channels { get; } = new();
    }
}
