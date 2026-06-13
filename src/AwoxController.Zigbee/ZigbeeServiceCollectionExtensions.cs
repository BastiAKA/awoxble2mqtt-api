using AwoxController.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AwoxController.Zigbee;

public static class ZigbeeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Zigbee2MQTT connection and the Zigbee light service.
    /// Both are singletons and hosted services, so they start with the app.
    /// Call from Program.cs: builder.Services.AddZigbeeLighting(builder.Configuration);
    /// </summary>
    public static IServiceCollection AddZigbeeLighting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<Zigbee2MqttOptions>(configuration.GetSection(Zigbee2MqttOptions.SectionName));

        // The MQTT connection: one instance, started as a hosted service.
        services.AddSingleton<Zigbee2MqttConnection>();
        services.AddHostedService(sp => sp.GetRequiredService<Zigbee2MqttConnection>());

        // The light service: same instance behind ILightBackend and as a hosted service
        // (so its constructor runs at startup and wires the MQTT event handler in time).
        // It is exposed as ILightBackend so CompositeLightService can collect it alongside
        // the BLE backend; the API depends on the composite ILightService, not on this directly.
        services.AddSingleton<ZigbeeLightService>();
        services.AddSingleton<ILightBackend>(sp => sp.GetRequiredService<ZigbeeLightService>());
        services.AddHostedService(sp => sp.GetRequiredService<ZigbeeLightService>());

        return services;
    }
}
