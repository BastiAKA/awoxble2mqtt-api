using System.Net.Http.Json;
using System.Text.Json;
using AwoxController.Core.Models;
using Microsoft.Extensions.Logging;

namespace AwoxController.Data;

/// <summary>
/// Imports mesh credentials + the device list from the AwoX/Eglo Parse cloud, using the same account
/// email/password as the phone app. Ported from scripts/Get-AwoxMeshCredentials.ps1. Touches only the
/// signed-in user's own account and devices.
/// </summary>
public sealed class AwoxCloudClient
{
    // Parse app keys (from github.com/fsaris/home-assistant-awox).
    private const string AppId = "55O69FLtoxPt67LLwaHGpHmVWndhZGn9Wty8PLrJ";
    private const string ClientKey = "PyR3yV65rytEicteNlQHSVNpAGvCByOrsLiEqJtI";
    private static readonly string[] Bases =
    {
        "https://l4hparse-prod.awox.cloud/parse/",      // AwoX/Eglo Connect
        "https://l4hparse-hc-prod.awox.cloud/parse/",   // AwoX HomeControl
    };

    private readonly HttpClient _http;
    private readonly ILogger<AwoxCloudClient> _logger;

    public AwoxCloudClient(HttpClient http, ILogger<AwoxCloudClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record ImportResult(List<MeshNetwork> Meshes, List<LampDevice> Lamps);

    /// <summary>Logs in, fetches mesh credentials + devices, and returns them mapped to entities.</summary>
    public async Task<ImportResult> FetchAsync(string email, string password, CancellationToken ct = default)
    {
        var installId = Guid.NewGuid().ToString();

        // --- login (try both backends) ---
        string? sessionToken = null, objectId = null, baseUrl = null;
        foreach (var b in Bases)
        {
            try
            {
                using var req = NewRequest(HttpMethod.Post, b + "login", installId, null);
                req.Content = JsonContent.Create(new { username = email, password, _method = "GET" });
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) { _logger.LogDebug("Login failed on {Base}: {Status}", b, resp.StatusCode); continue; }
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                sessionToken = doc.RootElement.GetProperty("sessionToken").GetString();
                objectId = doc.RootElement.GetProperty("objectId").GetString();
                baseUrl = b;
                _logger.LogInformation("AwoX cloud login OK via {Base}.", b);
                break;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Login attempt on {Base} threw.", b); }
        }

        if (sessionToken is null || objectId is null || baseUrl is null)
            throw new InvalidOperationException("AwoX cloud login failed on both endpoints — check the account email/password (the same you use in the app).");

        var whereBody = new
        {
            where = new { owner = new { __type = "Pointer", className = "_User", objectId } },
            _method = "GET",
        };

        var creds = await QueryAsync(baseUrl, "classes/Credential", installId, sessionToken, whereBody, ct);
        var devices = await QueryAsync(baseUrl, "classes/Device", installId, sessionToken, whereBody, ct);

        // --- map meshes ---
        var meshes = new List<MeshNetwork>();
        foreach (var c in creds)
        {
            var service = Str(c, "service");
            if (service is not ("mesh" or "zigbee")) continue;
            meshes.Add(new MeshNetwork
            {
                Service = service,
                MeshName = Str(c, "client_id") ?? "",
                MeshPassword = Str(c, "access_token") ?? "",
                MeshKey = Str(c, "refresh_token") ?? "",
            });
        }

        // --- map lamps ---
        var lamps = new List<LampDevice>();
        foreach (var d in devices)
        {
            var mac = Str(d, "macAddress");
            if (string.IsNullOrWhiteSpace(mac) || !d.TryGetProperty("address", out var addrEl) || addrEl.ValueKind == JsonValueKind.Null)
                continue;

            var meshId = ReadInt(addrEl);
            if (meshId < 0) meshId += 65536; // cloud returns signed 16-bit

            var type = Str(d, "type") ?? "";
            var protocol = type.Contains(".zigbee", StringComparison.OrdinalIgnoreCase) ? LightProtocol.Zigbee : LightProtocol.Tlmesh;
            var service = protocol == LightProtocol.Zigbee ? "zigbee" : "mesh";

            lamps.Add(new LampDevice
            {
                Name = (Str(d, "displayName") ?? mac).Trim(),
                Mac = mac.ToLowerInvariant(),
                MeshId = meshId,
                Protocol = protocol,
                Model = Str(d, "hardware") ?? "AwoX SmartLight",
                Mesh = new MeshNetwork { Service = service }, // carrier for FK resolution in ImportAsync
            });
        }

        _logger.LogInformation("AwoX cloud: {Meshes} mesh(es), {Lamps} device(s).", meshes.Count, lamps.Count);
        return new ImportResult(meshes, lamps);
    }

    private async Task<List<JsonElement>> QueryAsync(string baseUrl, string path, string installId, string sessionToken, object body, CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Post, baseUrl + path, installId, sessionToken);
        req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("results").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string installId, string? sessionToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("x-parse-application-id", AppId);
        req.Headers.Add("x-parse-client-key", ClientKey);
        req.Headers.Add("x-parse-installation-id", installId);
        if (sessionToken is not null)
            req.Headers.Add("x-parse-session-token", sessionToken);
        return req;
    }

    private static string? Str(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads an int from a JSON value that the cloud sends as either a Number or a String.</summary>
    private static int ReadInt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt32(out var n) => n,
        JsonValueKind.String when int.TryParse(e.GetString(), out var n) => n,
        _ => 0,
    };
}
