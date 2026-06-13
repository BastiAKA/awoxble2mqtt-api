using System.Text.Json;
using System.Text.Json.Serialization;

namespace AwoxController.MqttBridge;

/// <summary>JSON helpers shared across the bridge — web defaults (camelCase) + case-insensitive reads, so
/// the API's REST shape (camelCase) and SignalR's shape (PascalCase) both parse.</summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---- /api/devices ----------------------------------------------------------------------------------

/// <summary>A lamp as the API's GET /api/devices reports it (only the fields the bridge needs).</summary>
public sealed record ApiDevice(
    string? Id,
    string? Name,
    string? Room,
    string? Type,
    string? Mac,
    string? Model,
    bool Reachable,
    bool Enabled,
    string[]? Capabilities,
    ApiState? State)
{
    public bool Has(string capability) => Capabilities?.Contains(capability) == true;
}

/// <summary>The {on,brightness,colorBrightness,color,colorTemp} state shape used by REST.</summary>
public sealed record ApiState(bool? On, int? Brightness, int? ColorBrightness, ApiRgb? Color, int? ColorTemp);

public sealed record ApiRgb(int R, int G, int B);

// ---- /api/scenes -----------------------------------------------------------------------------------

public sealed record ApiScene(int Id, string Name);

// ---- SignalR StateChanged payload ({ deviceId, state }) --------------------------------------------

public sealed record StateChanged(string DeviceId, LiveState State);

/// <summary>The LightState the hub pushes (PascalCase over the wire — case-insensitive read handles it).</summary>
public sealed record LiveState(
    bool IsOn,
    int BrightnessPercent,
    int? ColorTempMireds,
    ApiRgb Color,
    bool IsColorMode);
