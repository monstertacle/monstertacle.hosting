using System.Net.Http.Headers;
using System.Text.Json;

namespace Monstertacle.Hosting.Tool;

internal static class TokenExchange
{
    internal const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    internal const string JwtType = "urn:ietf:params:oauth:token-type:jwt";
    internal const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const int MaximumResponseBytes = 32 * 1024;

    internal static async Task<string> GetAssertionAsync(HttpClient http, string? requestUrl, string? requestToken,
        string audience, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var endpoint) || !IsHttps(endpoint) || !endpoint.IsDefaultPort ||
            !endpoint.IdnHost.EndsWith(".actions.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(requestToken))
        {
            throw new ToolException("GitHub Actions OIDC credentials are unavailable.");
        }

        var builder = new UriBuilder(endpoint) { Query = (endpoint.Query.Length == 0 ? "" : endpoint.Query[1..] + "&") + "audience=" + Uri.EscapeDataString(audience) };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", requestToken);
        using var json = await ReadResponseAsync(http, request, "GitHub OIDC request failed.", cancellationToken);
        return ReadToken(json.RootElement, "value");
    }

    internal static async Task<string> ExchangeAsync(HttpClient http, Uri endpoint, string assertion, Uri resource,
        string scope, CancellationToken cancellationToken)
    {
        if (!IsHttps(endpoint) || !IsHttps(resource))
        {
            throw new ToolException("Token exchange requires HTTPS endpoints.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantType, ["subject_token_type"] = JwtType, ["subject_token"] = assertion,
            ["requested_token_type"] = AccessTokenType, ["resource"] = resource.AbsoluteUri, ["scope"] = scope
        });
        using var response = await ReadResponseAsync(http, request, "Token exchange failed.", cancellationToken);
        var body = response.RootElement;
        var token = ReadToken(body, "access_token");
        if (token == assertion || body.GetProperty("issued_token_type").GetString() != AccessTokenType ||
            !string.Equals(body.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !body.GetProperty("expires_in").TryGetInt32(out var expiry) || expiry is < 1 or > 300 ||
            (body.TryGetProperty("scope", out var returnedScope) && returnedScope.GetString() != scope))
        {
            throw new ToolException("The token service returned an unexpected credential.");
        }

        return token;
    }

    internal static bool IsHttps(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static string ReadToken(JsonElement body, string property)
    {
        var value = body.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16 * 1024 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw new ToolException("The credential response is invalid.");
        }

        return value;
    }

    private static async Task<JsonDocument> ReadResponseAsync(HttpClient http, HttpRequestMessage request,
        string error, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new ToolException(error);
        }

        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    internal static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };

    internal static HttpClient CreateClient(HttpMessageHandler handler, bool disposeHandler = true) => new(handler, disposeHandler)
    {
        MaxResponseContentBufferSize = MaximumResponseBytes,
        Timeout = TimeSpan.FromSeconds(15)
    };
}

internal sealed class ToolException(string message) : Exception(message);
