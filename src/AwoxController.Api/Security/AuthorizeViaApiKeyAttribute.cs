using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AwoxController.Api.Security;

/// <summary>
/// Gates a controller (or action) behind a static API key read from configuration (<c>Auth:ApiKey</c>).
/// <para>
/// If the configured key is empty/whitespace the API is <b>open</b> — no check at all. This keeps the
/// default LAN-appliance behaviour: set a key in <c>appsettings.json</c> only when you want protection.
/// </para>
/// When a key is configured, the caller must present it via any of:
/// <list type="bullet">
///   <item><c>X-Api-Key: &lt;key&gt;</c> header</item>
///   <item><c>Authorization: Bearer &lt;key&gt;</c> header</item>
///   <item><c>?apiKey=&lt;key&gt;</c> query string (handy for TV/widget embeds that can't set headers)</item>
/// </list>
/// A missing/wrong key yields 401. The comparison is constant-time.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AuthorizeViaApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";
    public const string QueryName = "apiKey";
    private const string ConfigKey = "Auth:ApiKey";

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configured = config[ConfigKey];

        // No key configured → API is open. This is the documented default.
        if (string.IsNullOrWhiteSpace(configured))
            return Task.CompletedTask;

        if (TryGetPresentedKey(context.HttpContext.Request, out var presented)
            && FixedTimeEquals(presented, configured))
            return Task.CompletedTask;

        context.Result = new UnauthorizedObjectResult(new
        {
            error = "Missing or invalid API key.",
            hint = $"Send the key via the '{HeaderName}' header, 'Authorization: Bearer <key>', or '?{QueryName}='.",
        });
        return Task.CompletedTask;
    }

    private static bool TryGetPresentedKey(HttpRequest request, out string key)
    {
        if (request.Headers.TryGetValue(HeaderName, out var headerVal) && !string.IsNullOrEmpty(headerVal))
        {
            key = headerVal.ToString();
            return true;
        }

        var auth = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            key = auth[bearer.Length..].Trim();
            return key.Length > 0;
        }

        if (request.Query.TryGetValue(QueryName, out var queryVal) && !string.IsNullOrEmpty(queryVal))
        {
            key = queryVal.ToString();
            return true;
        }

        key = "";
        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
