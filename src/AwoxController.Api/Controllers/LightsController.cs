using AwoxController.Api.Security;
using AwoxController.Core.Interfaces;
using AwoxController.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AuthorizeViaApiKey]
public sealed class LightsController : ControllerBase
{
    private readonly ILightService _lights;

    public LightsController(ILightService lights) => _lights = lights;

    /// <summary>GET /api/lights — all known devices.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyCollection<LightDevice>> GetAll() => Ok(_lights.GetDevices());

    /// <summary>GET /api/lights/{id}/state — last cached state.</summary>
    [HttpGet("{id}/state")]
    public ActionResult<LightState> GetState(string id)
        => _lights.TryGetState(id, out var state) ? Ok(state) : NotFound();

    [HttpPost("{id}/on")]
    public async Task<IActionResult> On(string id, CancellationToken ct)
    {
        await _lights.SetPowerAsync(id, true, ct);
        return Accepted();
    }

    [HttpPost("{id}/off")]
    public async Task<IActionResult> Off(string id, CancellationToken ct)
    {
        await _lights.SetPowerAsync(id, false, ct);
        return Accepted();
    }

    [HttpPost("{id}/toggle")]
    public async Task<IActionResult> Toggle(string id, CancellationToken ct)
    {
        await _lights.ToggleAsync(id, ct);
        return Accepted();
    }

    /// <summary>PUT /api/lights/{id}/brightness — body: { "percent": 0-100 }</summary>
    [HttpPut("{id}/brightness")]
    public async Task<IActionResult> Brightness(string id, [FromBody] BrightnessRequest req, CancellationToken ct)
    {
        await _lights.SetBrightnessAsync(id, req.Percent, ct);
        return Accepted();
    }

    /// <summary>PUT /api/lights/{id}/color — body: { "r": 0-255, "g": 0-255, "b": 0-255 }</summary>
    [HttpPut("{id}/color")]
    public async Task<IActionResult> Color(string id, [FromBody] ColorRequest req, CancellationToken ct)
    {
        await _lights.SetColorAsync(id, new RgbColor(req.R, req.G, req.B), ct);
        return Accepted();
    }

    /// <summary>PUT /api/lights/{id}/color-temp — body: { "mireds": 153-500 }</summary>
    [HttpPut("{id}/color-temp")]
    public async Task<IActionResult> ColorTemp(string id, [FromBody] ColorTempRequest req, CancellationToken ct)
    {
        await _lights.SetColorTemperatureAsync(id, req.Mireds, ct);
        return Accepted();
    }

    public sealed record BrightnessRequest(int Percent);
    public sealed record ColorRequest(byte R, byte G, byte B);
    public sealed record ColorTempRequest(int Mireds);
}
