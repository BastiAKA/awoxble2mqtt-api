using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using AwoxController.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

/// <summary>
/// Runtime-tunable settings, backed by the <c>app_settings</c> table (one string value per key,
/// parsed by the consumer). Lets you change values like the BLE poll interval without a redeploy.
/// </summary>
[ApiController]
[Route("api/settings")]
[AuthorizeViaApiKey]
public sealed class SettingsController : ControllerBase
{
    private readonly IAppSettings _settings;

    public SettingsController(IAppSettings settings) => _settings = settings;

    /// <summary>GET /api/settings — all persisted settings.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<SettingDto>> List(CancellationToken ct)
        => (await _settings.GetAllAsync(ct)).Select(SettingDto.From).ToList();

    /// <summary>
    /// PUT /api/settings/{key} — insert or update a setting. Body: { "value": "..." }. The key is
    /// taken from the route, so any key can be set (the consumer decides how to parse it).
    /// </summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<SettingDto>> Upsert(string key, [FromBody] SetSettingRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest("Key is required.");
        if (req?.Value is null)
            return BadRequest("Body must contain a 'value'.");

        await _settings.SetAsync(key.Trim(), req.Value, req.Description, ct);
        return new SettingDto(key.Trim(), req.Value, req.Description, DateTime.UtcNow);
    }
}

/// <summary>A persisted setting as returned by the API.</summary>
public sealed record SettingDto(string Key, string Value, string? Description, DateTime UpdatedUtc)
{
    public static SettingDto From(AppSetting s) => new(s.Key, s.Value, s.Description, s.UpdatedUtc);
}

/// <summary>Body for PUT /api/settings/{key}.</summary>
public sealed class SetSettingRequest
{
    public string? Value { get; set; }
    public string? Description { get; set; }
}
