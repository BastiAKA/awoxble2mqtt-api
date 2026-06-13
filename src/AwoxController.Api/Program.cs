using AwoxController.Api.Hubs;
using AwoxController.Api.Notifications;
using AwoxController.Ble;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Services;
using AwoxController.Data;
using AwoxController.Zigbee;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Controllers (LightsController) + JSON.
builder.Services.AddControllers();

// OpenAPI / Swagger UI for trying endpoints during development.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

// Transport backends. Each "Enabled" flag is read from configuration here so a backend is only
// registered (and started) when switched on — no broker/adapter, no connection attempts, no log
// noise. Each registers as ILightBackend; the CompositeLightService collects whichever are active
// and routes per device, so the API only ever depends on ILightService.
var zigbeeEnabled = builder.Configuration.GetValue<bool>($"{Zigbee2MqttOptions.SectionName}:Enabled");
var bleEnabled = builder.Configuration.GetValue<bool>($"{AwoxBleOptions.SectionName}:Enabled");

if (zigbeeEnabled)
    builder.Services.AddZigbeeLighting(builder.Configuration);

// BLE options + transport (connection, scanner) are registered unconditionally so discovery and
// direct MAC-addressed control work even when the auto-started light backend is off. The hosted
// light backend itself is only added when enabled.
builder.Services.AddAwoxBle(builder.Configuration);

if (bleEnabled)
    builder.Services.AddAwoxBleLighting();

builder.Services.AddSingleton<ILightService, CompositeLightService>();

// Runtime-tunable settings (app_settings table). Registered UNCONDITIONALLY because the BLE
// singletons depend on it; when no DB is configured it gracefully returns code defaults.
builder.Services.AddSingleton<IAppSettings, AppSettingsService>();

// Device registry. Provider via Database:Provider — "mysql" (needs ConnectionStrings:AwoxDb) or
// "sqlite" (zero-server local file). Registered when a provider/connection is available; the BLE
// control + scan still work without it (direct MAC endpoints), so a missing DB is non-fatal.
var dbProvider = (builder.Configuration["Database:Provider"] ?? "mysql").Trim().ToLowerInvariant();
var dbConfigured = dbProvider == "sqlite"
    || !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("AwoxDb"));
if (dbConfigured)
    builder.Services.AddAwoxData(builder.Configuration);

// CORS so browser-based clients (e.g. the Samsung TV Smart Hub app) can call the API from
// another origin. Lock it down by listing origins in Cors:AllowedOrigins; an empty list means
// "any origin" — fine for a LAN appliance with no cookies/credentials.
const string CorsPolicy = "AwoxCors";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
{
    policy.AllowAnyHeader().AllowAnyMethod();
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins);
    else
        policy.AllowAnyOrigin();
}));

// Live status push: SignalR hub (LightHub at /hubs/lights) + the notifier the light backends call on
// every real state change. In-memory fan-out only — no persistence, so the high-frequency advert
// status stream never touches the SD card.
builder.Services.AddSignalR();
// The notifier fans out to: (1) SignalR broadcast (live UI push) and (2) an in-memory state cache
// keyed by MAC. The device registry reads that cache back so REST /api/devices returns each lamp's
// real current state on first load (advert scan + control path) — with NO DB write, because the
// registry sits on an SD card and the advert stream is high-frequency.
builder.Services.AddSingleton<ILightStateCache, InMemoryLightStateCache>();
// Gateway election: pick a reachable mesh node to connect to (not an offline target). Scoped — it
// reads the device store (scoped). Used by the control + scenes controllers.
builder.Services.AddScoped<AwoxController.Api.Services.IMeshGatewayResolver, AwoxController.Api.Services.MeshGatewayResolver>();
builder.Services.AddSingleton<SignalRLightStateNotifier>();
builder.Services.AddSingleton<CachingLightStateNotifier>();
builder.Services.AddSingleton<ILightStateNotifier>(sp => new CompositeLightStateNotifier(
    sp.GetRequiredService<SignalRLightStateNotifier>(),
    sp.GetRequiredService<CachingLightStateNotifier>()));

var app = builder.Build();

// Apply EF Core migrations on startup so the schema exists. Best-effort: if the DB is unreachable
// we log and continue, because BLE control doesn't depend on it.
if (dbConfigured)
{
    try
    {
        var bootstrap = (builder.Configuration["Database:Bootstrap"] ?? "migrate").Trim().ToLowerInvariant();
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AwoxDbContext>()
            .EnsureDatabaseAsync(ensureCreated: bootstrap == "ensurecreated");
        app.Logger.LogInformation("Device registry ready (provider: {Provider}, bootstrap: {Bootstrap}).", dbProvider, bootstrap);

        // Seed tunable defaults so they show up in the settings UI (no-op if already present).
        var settings = app.Services.GetRequiredService<IAppSettings>();
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BlePollIntervalSeconds,
            AppSettingKeys.BlePollIntervalSecondsDefault.ToString(),
            "BLE status poll/keepalive interval in seconds (min 1). Takes effect on the next bulb (re)connect.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleOfflineAfterSeconds,
            AppSettingKeys.BleOfflineAfterSecondsDefault.ToString(),
            "Seconds since a lamp was last seen before it counts as OFFLINE (\"safe off\"). Long on purpose — lamps advertise intermittently; only a real power-off should read as offline.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleRelayVerifyEnabled,
            AppSettingKeys.BleRelayVerifyEnabledDefault.ToString().ToLowerInvariant(),
            "Var1 relay-verify master switch: relay a command through an already-held same-mesh node and confirm via the target's advert, instead of always reconnecting directly. Off = pure direct-connect.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleRelayVerifyTimeoutMs,
            AppSettingKeys.BleRelayVerifyTimeoutMsDefault.ToString(),
            "Milliseconds to wait for the target's confirming advert after a relayed command before declaring the relay path unreachable and falling back to a direct reconnect + resend.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleRelayReachableTtlSeconds,
            AppSettingKeys.BleRelayReachableTtlSecondsDefault.ToString(),
            "Seconds a CONFIRMED relay path is trusted before re-confirming. Keep short: the fast path relays WITHOUT a check, so a silently-broken path drives the lamp blind for this window. 0/negative = verify every command.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleRelayUnreachableTtlSeconds,
            AppSettingKeys.BleRelayUnreachableTtlSecondsDefault.ToString(),
            "Seconds a FAILED relay path is trusted before re-probing. Short, because relay failures are usually transient (congestion, lamp briefly off) — a negative verdict must not stick until restart.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleAdvertFastPollMs,
            AppSettingKeys.BleAdvertFastPollMsDefault.ToString(),
            "Advert-scan poll period (ms) WHILE a confirmation is awaited (relay-verify in flight). Lamps advertise a change instantly; at the normal multi-second poll we'd read BlueZ's cache too late and miss it. Idle, the scan stays on ble.poll_interval_seconds.");
        await settings.EnsureDefaultAsync(
            AppSettingKeys.BleConnectSettleMs,
            AppSettingKeys.BleConnectSettleMsDefault.ToString(),
            "Pause (ms) after stopping LE discovery before a cold connect (else le-connection-abort-by-local). Was 1.5s for the flaky Pi-3 onboard radio; the BT500 dongle needs far less. Paid on every cold connect — dial down carefully.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Could not migrate/connect the device database — device registry unavailable this run.");
    }
}
else
{
    app.Logger.LogInformation("No 'ConnectionStrings:AwoxDb' configured — device registry disabled.");
}

var bleTransport = app.Services.GetService<IAwoxBleScanner>()?.GetType().Name ?? "none";
app.Logger.LogInformation("Light backends — Zigbee: {Zigbee}, BLE: {Ble} (scan transport: {Transport})",
    zigbeeEnabled ? "enabled" : "disabled",
    bleEnabled ? "enabled" : "disabled",
    bleTransport);

if (OperatingSystem.IsWindows() && bleTransport == nameof(AwoxController.Ble.BlueZBleScanner))
    app.Logger.LogWarning(
        "Running the BlueZ (Linux) BLE build on Windows — scans return empty. " +
        "Launch the net10.0-windows10.0.19041.0 target to use the WinRT scanner.");

// OpenAPI document (/openapi/v1.json) + interactive Swagger UI (/swagger) for debugging.
// Available in every environment because this runs as a private LAN appliance; gate it behind
// IsDevelopment() if that ever changes.
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "AwoxController API");
    options.DocumentTitle = "AwoxController API";
});

if (app.Environment.IsDevelopment())
{
    // Only redirect to HTTPS during local development. On the Pi the API is served over plain
    // HTTP on the LAN (a Samsung TV won't trust a self-signed cert), so a redirect would break it.
    app.UseHttpsRedirection();
}

app.UseCors(CorsPolicy);

app.MapControllers();

// Live status push endpoint (clients subscribe to "StateChanged"). After UseCors so browser clients
// (Angular frontend / TV) can connect cross-origin.
app.MapHub<LightHub>("/hubs/lights");

app.Run();
