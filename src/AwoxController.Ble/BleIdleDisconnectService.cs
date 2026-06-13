using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AwoxController.Ble;

/// <summary>
/// Drops the held BLE gateway link after a short window of command inactivity, so the passive advert
/// scan resumes (a held connection pauses the scan AND makes the connected Connect-Z/-C lamp throttle
/// its own status advertising) and the AwoX phone app can reclaim the bulb. A clean disconnect leaves
/// the bulb advertising, so the next command reconnects normally.
///
/// The window is runtime-tunable via the <c>app_settings</c> key
/// <see cref="AppSettingKeys.BleIdleDisconnectSeconds"/> (re-read every tick), defaulting to the
/// <c>AwoxBle:MaxIdleDisconnectSeconds</c> option when set, else
/// <see cref="AppSettingKeys.BleIdleDisconnectSecondsDefault"/>. 0 or less = hold the link forever.
/// </summary>
public sealed class BleIdleDisconnectService : BackgroundService
{
    private readonly IAwoxBleConnection _connection;
    private readonly AwoxBleOptions _options;
    private readonly IAppSettings _settings;
    private readonly ILogger<BleIdleDisconnectService> _logger;

    public BleIdleDisconnectService(IAwoxBleConnection connection, IOptions<AwoxBleOptions> options,
        IAppSettings settings, ILogger<BleIdleDisconnectService> logger)
    {
        _connection = connection;
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fixed short cadence so a link that just went idle drops promptly and runtime setting changes
        // take effect within a tick. The threshold itself is read per-tick from app_settings.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        _logger.LogInformation("BLE idle-disconnect active (window from app_settings '{Key}', default {Default}s).",
            AppSettingKeys.BleIdleDisconnectSeconds, AppSettingKeys.BleIdleDisconnectSecondsDefault);

        // Fallback chain: DB setting → the legacy option (if >0) → the code default.
        var fallback = _options.MaxIdleDisconnectSeconds > 0
            ? _options.MaxIdleDisconnectSeconds
            : AppSettingKeys.BleIdleDisconnectSecondsDefault;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var idleSeconds = _settings.GetInt(AppSettingKeys.BleIdleDisconnectSeconds, fallback);
                if (idleSeconds <= 0) continue; // hold the link (auto-disconnect off)

                var last = _connection.LastActivityUtc;
                if (_connection.IsConnected && last is { } t && DateTimeOffset.UtcNow - t >= TimeSpan.FromSeconds(idleSeconds))
                {
                    _logger.LogInformation("BLE session idle ≥{Idle}s — disconnecting so the advert scan resumes / the app can reclaim the bulb.", idleSeconds);
                    try { await _connection.DisconnectAsync(stoppingToken); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Idle disconnect failed."); }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
