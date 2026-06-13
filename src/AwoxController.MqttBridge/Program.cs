using AwoxController.MqttBridge;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
builder.Services.AddSingleton(sp => new HaDiscovery(sp.GetRequiredService<IOptions<BridgeOptions>>().Value.Mqtt));
builder.Services.AddHttpClient<AwoxApiClient>();
builder.Services.AddHostedService<MqttBridgeWorker>();

builder.Build().Run();
