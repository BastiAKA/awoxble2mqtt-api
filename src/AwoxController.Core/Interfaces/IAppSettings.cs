using AwoxController.Core.Models;

namespace AwoxController.Core.Interfaces;

/// <summary>Well-known <see cref="AppSetting.Key"/> values, with their code defaults.</summary>
public static class AppSettingKeys
{
    /// <summary>BLE status-poll / keepalive interval in seconds. Clamped to a sane minimum in the backend.</summary>
    public const string BlePollIntervalSeconds = "ble.poll_interval_seconds";

    /// <summary>Default poll interval used when the key is absent from the DB.</summary>
    public const int BlePollIntervalSecondsDefault = 5;

    /// <summary>
    /// Seconds of command inactivity after which the held BLE gateway link is dropped, so the passive
    /// advert scan resumes (a held connection pauses the scan AND makes the connected lamp throttle its
    /// own status advertising). Keep it short so remote/app changes become visible quickly; long enough
    /// that a burst of commands reuses one connection. 0 or less = never auto-disconnect (hold forever).
    /// </summary>
    public const string BleIdleDisconnectSeconds = "ble.idle_disconnect_seconds";

    /// <summary>Default idle-disconnect window when the key is absent from the DB.</summary>
    public const int BleIdleDisconnectSecondsDefault = 12;

    /// <summary>
    /// Seconds since a lamp was last seen (a live advert, or a command) after which it is treated as
    /// OFFLINE ("safe off" — really powered down). Deliberately long: lamps advertise only
    /// intermittently and the advert scan pauses while a command link is held, so a short window would
    /// flap a perfectly powered lamp to offline. Only a genuine power-off (no sighting for this long)
    /// should read as offline. Also gates the gateway-relay rerouting (only reroute around a lamp that's
    /// been silent this long).
    /// </summary>
    public const string BleOfflineAfterSeconds = "ble.offline_after_seconds";

    /// <summary>Default "really off" window when the key is absent from the DB (10 minutes).</summary>
    public const int BleOfflineAfterSecondsDefault = 600;

    /// <summary>
    /// Master switch for Var1 relay-verify: when a session is already held on a same-mesh node, relay a
    /// command through it and confirm via the target's advert (learning which host→target pairs work)
    /// instead of always reconnecting directly to the target. Off = pure direct-connect (the prior
    /// behaviour). Runtime-tunable so a misbehaving radio can be reverted without a redeploy.
    /// </summary>
    public const string BleRelayVerifyEnabled = "ble.relay_verify_enabled";

    /// <summary>Default for <see cref="BleRelayVerifyEnabled"/> — on.</summary>
    public const bool BleRelayVerifyEnabledDefault = true;

    /// <summary>
    /// How long to wait for the target lamp's confirming advert after a relayed command before declaring
    /// the (host→target) relay path unreachable and falling back to a direct reconnect + resend.
    /// </summary>
    public const string BleRelayVerifyTimeoutMs = "ble.relay_verify_timeout_ms";

    /// <summary>Default confirm window when the key is absent from the DB (5 s). Generous on purpose: it
    /// runs on the queue worker AFTER the 202, so the user never waits on it — and some lamps (e.g.
    /// Badezimmer Spiegel 2) advertise their change slowly, so a tight window would falsely read them as
    /// unconfirmed/unreachable.</summary>
    public const int BleRelayVerifyTimeoutMsDefault = 5000;

    /// <summary>
    /// How long a CONFIRMED (host→target) relay verdict is trusted before it must be re-verified. Keep it
    /// SHORT: on the confirmed fast path the command is relayed WITHOUT an advert check, so if the relay
    /// path silently breaks (lamp moved, gateway dropped) the target is driven blind for this whole
    /// window. A short value bounds "lamp unsteerable" to seconds while still letting a burst of commands
    /// (e.g. a slider drag) reuse one confirmation. 0 or less = verify every command (never suspend).
    /// </summary>
    public const string BleRelayReachableTtlSeconds = "ble.relay_reachable_ttl_seconds";

    /// <summary>Default trust window for a confirmed relay (30 s) — confirmation only briefly suspended.</summary>
    public const int BleRelayReachableTtlSecondsDefault = 30;

    /// <summary>
    /// How long an UNREACHABLE (host→target) relay verdict is trusted before the pair is re-probed. Short:
    /// a failed relay is usually transient (mesh congestion, the lamp was briefly off, a too-tight confirm
    /// window), so a negative verdict must NOT stick for the whole session — re-probe it periodically.
    /// </summary>
    public const string BleRelayUnreachableTtlSeconds = "ble.relay_unreachable_ttl_seconds";

    /// <summary>Default re-probe window for a failed relay (2 min).</summary>
    public const int BleRelayUnreachableTtlSecondsDefault = 120;

    /// <summary>
    /// Advert-scan poll period (ms) while a confirmation is being awaited (i.e. the advert stream has a
    /// subscriber — a relay-verify in flight). The lamps emit a state advert the instant they change, but
    /// the scan only READS BlueZ's cached advert each tick; at the normal multi-second poll that change
    /// advert is missed inside the short verify window. So while someone is waiting, the scan runs fast
    /// enough to catch it. Idle (no subscriber) the scan stays on the economical
    /// <see cref="BlePollIntervalSeconds"/> cadence to spare the dongle.
    /// </summary>
    public const string BleAdvertFastPollMs = "ble.advert_fast_poll_ms";

    /// <summary>Default fast advert-poll period while awaiting a confirmation (400 ms).</summary>
    public const int BleAdvertFastPollMsDefault = 400;

    /// <summary>
    /// Pause (ms) after stopping LE discovery before a cold connect, to let the controller settle — the
    /// LE connect aborts (le-connection-abort-by-local) if it fires while the scan is still winding down.
    /// This was tuned high (1.5s) for the flaky Pi-3 ONBOARD radio; with the USB BT500 dongle + the
    /// IsBusy guard that already stops the advert scan restarting discovery mid-connect, it can be much
    /// shorter. Tunable so it can be dialled on hardware without a redeploy. Paid on EVERY cold connect,
    /// so it directly sets the floor on command latency when no session is held.
    /// </summary>
    public const string BleConnectSettleMs = "ble.connect_settle_ms";

    /// <summary>Default settle after stopping discovery before a cold connect (600 ms).</summary>
    public const int BleConnectSettleMsDefault = 600;

    /// <summary>
    /// Max BLE GATT sessions the connection pool may hold at once — one per mesh. Lets commands for
    /// DIFFERENT meshes be sent concurrently (each on its own held session) instead of disconnect+
    /// reconnecting on every mesh switch; the command queue drains up to this many meshes in parallel.
    /// Within ONE mesh a single session already fans out (relay/broadcast), so extra connections there
    /// only add dongle churn — concurrency is across meshes only. <c>1</c> = legacy behaviour (one held
    /// session, reconnect on mesh switch, serial drain). Kept low by default for the flaky Pi dongle;
    /// raise once stable. Proven feasible 2026-06-14 (two simultaneous links across meshes).
    /// </summary>
    public const string BleMaxConnections = "ble.max_connections";

    /// <summary>Default max concurrent per-mesh BLE sessions (2 — conservative for the Pi dongle).</summary>
    public const int BleMaxConnectionsDefault = 2;
}

/// <summary>
/// Runtime-tunable settings backed by the <c>app_settings</c> table. Read access is from an in-memory
/// cache (cheap, safe to call from hot paths like the BLE keepalive timer); writes go to the DB and
/// refresh the cache. When no database is configured/reachable, reads fall back to the supplied
/// defaults so the rest of the app keeps working.
/// </summary>
public interface IAppSettings
{
    /// <summary>The raw string value, or <paramref name="fallback"/> if the key is absent.</summary>
    string? GetString(string key, string? fallback = null);

    /// <summary>The value parsed as an int (invariant culture), or <paramref name="fallback"/>.</summary>
    int GetInt(string key, int fallback);

    /// <summary>The value parsed as a double (invariant culture), or <paramref name="fallback"/>.</summary>
    double GetDouble(string key, double fallback);

    /// <summary>The value parsed as a bool ("true"/"false"/"1"/"0"), or <paramref name="fallback"/>.</summary>
    bool GetBool(string key, bool fallback);

    /// <summary>All persisted settings, key-ordered. Throws if no database is configured.</summary>
    Task<IReadOnlyList<AppSetting>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Inserts or updates <paramref name="key"/> and refreshes the cache.</summary>
    Task SetAsync(string key, string value, string? description = null, CancellationToken ct = default);

    /// <summary>Inserts <paramref name="key"/> with <paramref name="value"/> only if it doesn't exist yet (seed defaults).</summary>
    Task EnsureDefaultAsync(string key, string value, string? description = null, CancellationToken ct = default);

    /// <summary>Drops the in-memory cache so the next read reloads from the DB.</summary>
    void Reload();
}
