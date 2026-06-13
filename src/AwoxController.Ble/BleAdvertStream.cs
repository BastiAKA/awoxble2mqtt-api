using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwoxController.Core.Models;

namespace AwoxController.Ble;

/// <summary>One observed advert: a lamp's live state, keyed by MAC, at the moment it was seen.</summary>
public sealed record AdvertUpdate(string Mac, LightState State, DateTime AtUtc);

/// <summary>
/// In-memory pub/sub of live advert state. The passive advert scan publishes every lamp sighting here;
/// callers subscribe to await a specific lamp reaching a state (the relay-verification signal) or watch
/// the whole stream. Fan-out: each subscriber gets its own bounded, drop-oldest channel, so a slow
/// consumer can never block the scan loop.
/// </summary>
public interface IBleAdvertStream
{
    /// <summary>Publishes a lamp's freshly-observed advert state to all subscribers. Called by the scan.</summary>
    void Publish(string mac, LightState state);

    /// <summary>True while at least one caller is awaiting/watching adverts (a relay-verify in flight). The
    /// advert scan reads its poll cadence off this — fast while someone needs a confirming advert, slow
    /// when idle — so an immediate change advert isn't missed inside the short verify window.</summary>
    bool HasSubscribers { get; }

    /// <summary>
    /// Subscribes IMMEDIATELY and completes <c>true</c> when <paramref name="mac"/> is next seen matching
    /// <paramref name="match"/>, or <c>false</c> when <paramref name="until"/> is cancelled (the caller's
    /// deadline). Call this BEFORE sending the command (so the confirming advert can't slip past), then
    /// start <paramref name="until"/>'s deadline AFTER the write is out — that way a slow cold connect
    /// inside the send doesn't eat the confirmation window.
    /// </summary>
    Task<bool> WatchUntilAsync(string mac, Func<LightState, bool> match, CancellationToken until);

    /// <summary>Streams every advert update until cancelled.</summary>
    IAsyncEnumerable<AdvertUpdate> Watch(CancellationToken ct = default);
}

public sealed class BleAdvertStream : IBleAdvertStream
{
    private readonly ConcurrentDictionary<Guid, Channel<AdvertUpdate>> _subscribers = new();

    public bool HasSubscribers => !_subscribers.IsEmpty;

    public void Publish(string mac, LightState state)
    {
        if (_subscribers.IsEmpty) return; // nobody listening — skip the work entirely

        // Snapshot: the scan mutates the same LightState instance every tick, so a subscriber that reads
        // it later would otherwise see newer values, not the state at this advert.
        var snapshot = new LightState
        {
            IsOn = state.IsOn,
            BrightnessPercent = state.BrightnessPercent,
            ColorTempMireds = state.ColorTempMireds,
            Color = state.Color,
            IsColorMode = state.IsColorMode,
            LastUpdatedUtc = state.LastUpdatedUtc,
        };
        var update = new AdvertUpdate(NormalizeMac(mac), snapshot, DateTime.UtcNow);
        foreach (var ch in _subscribers.Values)
            ch.Writer.TryWrite(update); // bounded + drop-oldest → never blocks
    }

    public async Task<bool> WatchUntilAsync(string mac, Func<LightState, bool> match, CancellationToken until)
    {
        var target = NormalizeMac(mac);
        try
        {
            await foreach (var u in Watch(until))
                if (u.Mac == target && match(u.State))
                    return true;
        }
        catch (OperationCanceledException)
        {
            // the deadline (or an outer cancel) fired → simply "not confirmed"
        }
        return false;
    }

    public async IAsyncEnumerable<AdvertUpdate> Watch([EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<AdvertUpdate>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = ch;
        try
        {
            await foreach (var u in ch.Reader.ReadAllAsync(ct))
                yield return u;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    // MAC without separators, lower-cased — matches the state cache's key form.
    private static string NormalizeMac(string mac) => mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
}
