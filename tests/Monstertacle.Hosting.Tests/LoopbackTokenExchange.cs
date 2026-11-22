using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Monstertacle.Hosting.Security;

namespace Monstertacle.Hosting.Tests;

internal sealed class LoopbackTokenExchange(
    GitHubWorkloadTrust trust, Func<CancellationToken, Task<IEnumerable<SecurityKey>>> githubKeys,
    X509Certificate2 server, string resource, string scope, string subject) : IAsyncDisposable
{
    internal const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    internal const string JwtType = "urn:ietf:params:oauth:token-type:jwt";
    internal const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    private const int TokenSeconds = 120;
    private readonly RSA signing = RSA.Create(2048);
    private WebApplication? app;
    public string Issuer => app!.Urls.Single().TrimEnd('/');
    public Uri Endpoint => new(Issuer + "/token");

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(server)));
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
        app = builder.Build();
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new { issuer = Issuer, jwks_uri = Issuer + "/jwks" }));
        app.MapGet("/jwks", () =>
        {
            var key = signing.ExportParameters(false);
            return Results.Json(new { keys = new[] { new
            {
                kty = "RSA", kid = "exchange", alg = "RS256", use = "sig",
                n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent)
            } } });
        });
        app.MapPost("/token", (Delegate)ExchangeAsync);
        await app.StartAsync();
    }

    private async Task<IResult> ExchangeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (!string.Equals(context.Request.ContentType, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form.Count != 6 || !Has(form, "grant_type", GrantType) || !Has(form, "subject_token_type", JwtType) ||
            !Has(form, "requested_token_type", AccessTokenType) || !Has(form, "resource", resource) ||
            !Has(form, "scope", scope) || form["subject_token"] is not { Count: 1 } assertion ||
            string.IsNullOrEmpty(assertion[0]))
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        if (!await trust.AcceptsAsync(assertion[0]!, await githubKeys(context.RequestAborted)))
        {
            return Results.BadRequest(new { error = "invalid_grant" });
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(Issuer, resource,
            [new("sub", subject), new("scope", scope),
             new("iat", EpochTime.GetIntDate(now).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
            now, now.AddSeconds(TokenSeconds), new SigningCredentials(new RsaSecurityKey(signing) { KeyId = "exchange" }, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return Results.Json(new
        {
            access_token = new JwtSecurityTokenHandler().WriteToken(token), issued_token_type = AccessTokenType,
            token_type = "Bearer", expires_in = TokenSeconds, scope
        });
    }

    private static bool Has(IFormCollection form, string name, string value) =>
        form[name] is { Count: 1 } values && values[0] == value;

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync();
        }
        signing.Dispose();
    }
}
