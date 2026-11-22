using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Monstertacle.Hosting.Security;

namespace Monstertacle.Hosting.McpHost;

internal sealed class PublicIngress(UserTokenSettings tokens, BrokerPolicy policy)
{
    public static PublicIngress? Create(IConfiguration settings, UserTokenSettings tokens, BrokerPolicy policy)
    {
        var ingress = settings.GetSection("PublicIngress");
        if (!ingress.GetValue<bool>("Enabled"))
        {
            return null;
        }

        var name = ingress["Hostname"];
        if (name is null || Uri.CheckHostName(name) != UriHostNameType.Dns ||
            !string.Equals(tokens.Audience, $"https://{name}/mcp", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Public ingress requires the exact HTTPS /mcp audience on port 443.");
        }

        return new PublicIngress(tokens, policy);
    }

    public bool HasHumanGrant() => policy.Read()?.People.Any(person =>
        person.Grants.Length > 0 && person.Identities.Any(identity => identity.Issuer == tokens.Issuer)) == true;

    public async Task<bool> IsReadyAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!HasHumanGrant())
        {
            return false;
        }

        var bearer = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var manager = bearer.Get(JwtBearerDefaults.AuthenticationScheme).ConfigurationManager;
            var configuration = manager is null ? null : await manager.GetConfigurationAsync(deadline.Token);
            return configuration is { SigningKeys.Count: > 0 } && configuration.Issuer == tokens.Issuer;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or
                                          InvalidOperationException or ArgumentException or
                                          System.Text.Json.JsonException or OperationCanceledException)
        {
            return false;
        }
    }

    internal sealed class StartupCheck(PublicIngress ingress, IServiceProvider services) : IHostedLifecycleService
    {
        // The host runs StartingAsync before any hosted service starts listening.
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            if (!await ingress.IsReadyAsync(services, cancellationToken))
            {
                throw new InvalidOperationException("Public ingress requires a human grant and verified issuer signing keys.");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
