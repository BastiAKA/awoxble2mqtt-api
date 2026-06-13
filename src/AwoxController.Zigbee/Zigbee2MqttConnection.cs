using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace AwoxController.Zigbee;

/// <summary>
/// Owns the single long-lived MQTT connection to the broker. Runs as a hosted
/// service so it connects on app start and auto-reconnects on drop. Other services
/// publish through <see cref="PublishAsync"/> and listen via <see cref="MessageReceived"/>.
///
/// MQTTnet 4.x API. On MQTTnet 5.x, replace <c>MqttFactory</c> with <c>MqttClientFactory</c>.
/// </summary>
public sealed class Zigbee2MqttConnection : IHostedService, IAsyncDisposable
{
    private readonly Zigbee2MqttOptions _options;
    private readonly ILogger<Zigbee2MqttConnection> _logger;
    private readonly MqttFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    private CancellationTokenSource? _cts;

    /// <summary>Raised for every inbound message: (topic, payloadAsUtf8String).</summary>
    public event Func<string, string, Task>? MessageReceived;

    public string BaseTopic => _options.BaseTopic;

    public Zigbee2MqttConnection(
        IOptions<Zigbee2MqttOptions> options,
        ILogger<Zigbee2MqttConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = _factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithCleanSession();

        if (!string.IsNullOrWhiteSpace(_options.Username))
            builder = builder.WithCredentials(_options.Username, _options.Password);

        _clientOptions = builder.Build();

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Zigbee2MQTT disabled (Zigbee2Mqtt:Enabled = false); MQTT connection skipped.");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ConnectAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_client.IsConnected)
            await _client.DisconnectAsync();
    }

    public Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        return _client.PublishAsync(message, ct);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Connecting to MQTT broker {Host}:{Port}", _options.Host, _options.Port);
            await _client.ConnectAsync(_clientOptions, ct);

            // One wildcard subscription covers device state, the bridge device list,
            // and everything else under the base topic.
            var subscribe = _factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic($"{_options.BaseTopic}/#").WithAtLeastOnceQoS())
                .Build();

            await _client.SubscribeAsync(subscribe, ct);
            _logger.LogInformation("Subscribed to {Topic}/#", _options.BaseTopic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT connect failed; will retry on next disconnect cycle.");
        }
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_cts is null || _cts.IsCancellationRequested)
            return;

        _logger.LogWarning("MQTT disconnected ({Reason}); reconnecting in {Delay}s.",
            args.Reason, _options.ReconnectDelaySeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ConnectAsync(_cts.Token);
    }

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

        var handler = MessageReceived;
        if (handler is not null)
            await handler(topic, payload);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_client.IsConnected)
            await _client.DisconnectAsync();
        _client.Dispose();
    }
}
