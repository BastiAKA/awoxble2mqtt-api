using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AwoxController.Ble;

public static class AwoxBleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AwoX BLE options and the platform GATT transport (WinRT on Windows, BlueZ
    /// elsewhere): the connection and the scan/probe helper. Call this UNCONDITIONALLY — discovery
    /// and direct MAC-addressed control must work even when the auto-started light backend is off.
    /// </summary>
    public static IServiceCollection AddAwoxBle(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AwoxBleOptions>(configuration.GetSection(AwoxBleOptions.SectionName));

        // The platform transport is registered as TRANSIENT — the connection pool news up one instance per
        // mesh (up to ble.max_connections) so commands for different meshes run on their own held session.
        // Every consumer injects the pool (the IAwoxBleConnection singleton); credential-less calls route
        // to the default mesh, so the legacy/advert paths are unchanged. Cap=1 ⇒ exactly the old behaviour.
#if WINDOWS
        services.AddTransient<WindowsBleConnection>();
        services.AddSingleton<Func<IAwoxBleConnection>>(sp => () => sp.GetRequiredService<WindowsBleConnection>());
        services.AddSingleton<IAwoxBleScanner, WindowsBleScanner>();
#else
        services.AddTransient<BlueZBleConnection>();
        services.AddSingleton<Func<IAwoxBleConnection>>(sp => () => sp.GetRequiredService<BlueZBleConnection>());
        services.AddSingleton<IAwoxBleScanner, BlueZBleScanner>();
#endif
        services.AddSingleton<IAwoxBleConnection, PooledBleConnection>();

        // Idle-disconnect runs regardless of the light backend; it self-disables when
        // MaxIdleDisconnectSeconds <= 0, so it's safe to register unconditionally.
        services.AddHostedService<BleIdleDisconnectService>();

        // Command queue: the DB-routed control endpoints (DevicesController/ScenesController) enqueue
        // here and return 202, so it must exist whenever those controllers do (i.e. unconditionally).
        // One instance is both the queue and its draining worker.
        services.AddSingleton<BleCommandQueue>();
        services.AddSingleton<IBleCommandQueue>(sp => sp.GetRequiredService<BleCommandQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<BleCommandQueue>());

        // Live advert stream: the scan publishes every lamp sighting; callers await a lamp reaching a
        // state (relay verification). Singleton pub/sub, fed by BleLightService.ApplyAdvertStatus.
        services.AddSingleton<IBleAdvertStream, BleAdvertStream>();

        // Var1 relay-verify: the queue worker hands each command to the coordinator, which decides
        // relay-through-a-held-node (advert-verified) vs a direct connect, and learns reachable (H,T)
        // pairs. The learned map is a singleton so it survives across commands; behind the
        // ble.relay_verify_enabled flag (default on). TTLs read the app-settings on each lookup so they
        // stay runtime-tunable — short for unreachable so a transient relay failure is re-probed, not
        // pinned until restart.
        services.AddSingleton<RelayReachabilityMap>(sp =>
        {
            var settings = sp.GetRequiredService<IAppSettings>();
            return new RelayReachabilityMap(
                reachableTtl: () => TimeSpan.FromSeconds(settings.GetInt(
                    AppSettingKeys.BleRelayReachableTtlSeconds, AppSettingKeys.BleRelayReachableTtlSecondsDefault)),
                unreachableTtl: () => TimeSpan.FromSeconds(settings.GetInt(
                    AppSettingKeys.BleRelayUnreachableTtlSeconds, AppSettingKeys.BleRelayUnreachableTtlSecondsDefault)));
        });
        services.AddSingleton<IRelayCoordinator, RelayCoordinator>();

        return services;
    }

    /// <summary>
    /// Registers the auto-started BLE light backend (friendly-name device list, status push) as an
    /// <see cref="ILightBackend"/> for the CompositeLightService. Call only when AwoxBle:Enabled.
    /// Requires <see cref="AddAwoxBle"/> to have registered the transport + options.
    /// </summary>
    public static IServiceCollection AddAwoxBleLighting(this IServiceCollection services)
    {
        services.AddSingleton<BleLightService>();
        services.AddSingleton<ILightBackend>(sp => sp.GetRequiredService<BleLightService>());
        services.AddHostedService(sp => sp.GetRequiredService<BleLightService>());

        // Passive advertisement status scan: feeds live state into BleLightService without ever holding a
        // connection (and feeds the relay-verify advert stream). BlueZ polls the adapter cache; WinRT uses
        // a push-based advertisement watcher. Both self-disable when StatusScanEnabled is false.
#if WINDOWS
        services.AddHostedService<WindowsBleAdvStatusService>();
#else
        services.AddHostedService<BleAdvStatusService>();
#endif

        return services;
    }
}
