using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace AwoxController.Api.Controllers;

/// <summary>
/// Backend descriptor for the SmartHome Control API contract. Deliberately NOT behind the API key,
/// so an aggregator/BFF can detect the backend and the contract version it speaks before authenticating.
/// </summary>
[ApiController]
[Route("api/info")]
public sealed class InfoController : ControllerBase
{
    /// <summary>GET /api/info</summary>
    [HttpGet]
    public object Get() => new
    {
        name = "AwoX BLE",
        vendor = "awox-ble",
        version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0",
        contractVersion = "0.1",
    };
}
