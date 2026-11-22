using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Monstertacle.Communication.Mcp;
using Monstertacle.Hosting.Security;

namespace Monstertacle.Hosting.McpHost;

public static class McpApplication
{
    private const string McpRoute = "/mcp";
    private const string MetadataRoute = "/.well-known/oauth-protected-resource" + McpRoute;
    public const string BrokerClientName = "session-broker";

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection("Mcp");
        var tokens = new UserTokenSettings(settings.GetSection("Authentication"));
        var resource = new Uri(tokens.Audience);
        if (resource.AbsolutePath != McpRoute || resource.Query.Length > 0)
        {
            throw new InvalidOperationException("The MCP audience must be the public HTTPS /mcp resource URI.");
        }

        var metadataUri = resource.GetLeftPart(UriPartial.Authority) + MetadataRoute;
        var broker = new Uri(UserTokenSettings.RequireHttps(settings, "BrokerUrl"));
        if (broker.AbsolutePath != "/" || broker.Query.Length > 0)
        {
            throw new InvalidOperationException("The broker URL must be an HTTPS origin.");
        }

        var policy = new BrokerPolicy(settings, tokens.WorkloadIssuer);
        var publicIngress = PublicIngress.Create(settings, tokens, policy);
        tokens.Configure(builder.Services, saveToken: true);
        if (publicIngress is not null)
        {
            builder.Services.AddHostedService(services => new PublicIngress.StartupCheck(publicIngress, services));
        }
        builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            options.Events.OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{metadataUri}\"";
                return Task.CompletedTask;
            });
        builder.Services.AddSingleton<X509Certificate2>(_ =>
        {
            var path = settings["BrokerClientCertificateFile"];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("The MCP host requires a broker client certificate.");
            }

            return X509CertificateLoader.LoadPkcs12FromFile(path, settings["BrokerClientCertificatePassword"]);
        });
        builder.Services.AddHttpClient(BrokerClientName, client =>
            {
                client.BaseAddress = broker;
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(services =>
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
                handler.ClientCertificates.Add(services.GetRequiredService<X509Certificate2>());
                return handler;
            })
            .RemoveAllLoggers();
        builder.Services.AddScoped<BrokerSessionClient>();
        builder.Services.AddMonstertacleMcp().WithTools<SessionTools>();

        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<X509Certificate2>();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(McpRoute))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!context.Request.IsHttps)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (publicIngress is not null && !publicIngress.HasHumanGrant())
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }

                if (context.User.Identity?.IsAuthenticated != true)
                {
                    await context.ChallengeAsync();
                    return;
                }

                if (policy.Resolve(context.User) is null)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    return;
                }
            }

            await next(context);
        });
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.NoContent());
        app.MapGet(MetadataRoute, () => Results.Json(new
        {
            resource = tokens.Audience,
            authorization_servers = new[] { tokens.Issuer },
            scopes_supported = new[] { tokens.Scope },
            bearer_methods_supported = new[] { "header" }
        }));
        app.MapMonstertacleMcp(McpRoute).RequireAuthorization();
        if (publicIngress is not null)
        {
            app.MapGet("/.well-known/hosting-ingress-readiness", async (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return context.Request.IsHttps &&
                    await publicIngress.IsReadyAsync(context.RequestServices, context.RequestAborted)
                    ? Results.NoContent() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            });
        }
        return app;
    }
}
