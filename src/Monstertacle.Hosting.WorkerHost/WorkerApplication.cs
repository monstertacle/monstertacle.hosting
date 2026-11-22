using System.Security.Cryptography.X509Certificates;

namespace Monstertacle.Hosting.WorkerHost;

/// <summary>Composes a lane-bound worker using deployment-owned agent registrations.</summary>
public static class WorkerApplication
{
    /// <summary>Runs the worker. Its profile is taken solely from deployment configuration.</summary>
    public static async Task RunAsync(WebApplicationBuilder builder, IReadOnlyList<WorkerRegistration> registrations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registrations);
        var settings = builder.Configuration.GetSection("Worker");
        if (settings["StateDirectory"] is not { } directory ||
            !Guid.TryParse(settings["TrustScope"], out var trustScope) ||
            settings["BrokerClientCertificateFile"] is not { } certificateFile ||
            !Uri.TryCreate(settings["BrokerUrl"], UriKind.Absolute, out var brokerUrl) ||
            brokerUrl.Scheme != Uri.UriSchemeHttps || brokerUrl.AbsolutePath != "/" ||
            brokerUrl.UserInfo.Length != 0 || brokerUrl.Query.Length != 0 || brokerUrl.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Configure the worker's private state and HTTPS broker connection.");
        }

        var selected = SelectRegistration(settings["Profile"], registrations);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificateFile, settings["BrokerClientCertificatePassword"]);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.ClientCertificates.Add(certificate);
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = brokerUrl, Timeout = TimeSpan.FromSeconds(15) };
        await using var runtime = await WorkerRuntime.OpenAsync(directory, trustScope,
            registrations.Select(registration => registration.Provider).ToArray(),
            registrations.Select(registration => registration.Profile).ToArray(), cancellationToken);
        var ledger = new WorkerLedger(directory);
        builder.Services.AddSingleton(services => new WorkerDispatch(ledger, new WorkerBrokerClient(http), runtime,
            selected.Profile.AgentProfileId, selected.Provider.Provider.Capabilities.SupportsCancellation,
            services.GetRequiredService<ILogger<WorkerDispatch>>()));
        builder.Services.AddHostedService<WorkerPollingService>();
        var app = builder.Build();
        app.MapGet("/health/live", () => runtime.Completion.IsCompleted ? Results.StatusCode(503) : Results.NoContent());

        await app.RunAsync(cancellationToken);
    }

    internal static WorkerRegistration SelectRegistration(string? profile, IReadOnlyList<WorkerRegistration> registrations) =>
        registrations.SingleOrDefault(registration =>
            string.Equals(registration.Profile.AgentProfileId, profile, StringComparison.Ordinal)) ??
        throw new InvalidOperationException("Configure a registered worker profile.");
}
