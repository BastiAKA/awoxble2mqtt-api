using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace AwoxController.MqttBridge;

/// <summary>Thin typed wrapper over the AwoxController REST API. Lamps are addressed by MAC (the API
/// accepts id, name or MAC as the {key}); scenes by numeric id.</summary>
public sealed class AwoxApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AwoxApiClient> _logger;

    public AwoxApiClient(HttpClient http, IOptions<BridgeOptions> options, ILogger<AwoxApiClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.BaseAddress = new Uri(options.Value.ApiBaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", options.Value.ApiKey);
    }

    public async Task<IReadOnlyList<ApiDevice>> GetDevicesAsync(CancellationToken ct)
        => await _http.GetFromJsonAsync<List<ApiDevice>>("api/devices", Json.Opts, ct) ?? [];

    public async Task<IReadOnlyList<ApiScene>> GetScenesAsync(CancellationToken ct)
        => await _http.GetFromJsonAsync<List<ApiScene>>("api/scenes", Json.Opts, ct) ?? [];

    public Task OnAsync(string mac, CancellationToken ct) => PostAsync($"api/devices/{mac}/on", null, ct);
    public Task OffAsync(string mac, CancellationToken ct) => PostAsync($"api/devices/{mac}/off", null, ct);
    public Task ColorAsync(string mac, int r, int g, int b, CancellationToken ct) => PutAsync($"api/devices/{mac}/color", new { r, g, b }, ct);
    public Task ColorTempAsync(string mac, int mireds, CancellationToken ct) => PutAsync($"api/devices/{mac}/colorTemp", new { mireds }, ct);
    public Task BrightnessAsync(string mac, int percent, CancellationToken ct) => PutAsync($"api/devices/{mac}/brightness", new { percent }, ct);
    public Task ColorBrightnessAsync(string mac, int percent, CancellationToken ct) => PutAsync($"api/devices/{mac}/colorBrightness", new { percent }, ct);
    public Task ApplySceneAsync(int id, CancellationToken ct) => PostAsync($"api/scenes/{id}/apply", null, ct);

    private async Task PostAsync(string path, object? body, CancellationToken ct)
    {
        using var resp = body is null
            ? await _http.PostAsync(path, null, ct)
            : await _http.PostAsJsonAsync(path, body, Json.Opts, ct);
        EnsureOk(resp.StatusCode, "POST", path);
    }

    private async Task PutAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await _http.PutAsJsonAsync(path, body, Json.Opts, ct);
        EnsureOk(resp.StatusCode, "PUT", path);
    }

    private void EnsureOk(System.Net.HttpStatusCode code, string verb, string path)
    {
        // The control endpoints return 202 (queued); treat any non-success as a warning, don't throw — one
        // failed command must not stop the bridge from serving the rest.
        if ((int)code is < 200 or >= 300)
            _logger.LogWarning("API {Verb} {Path} returned {Code}.", verb, path, (int)code);
    }
}
