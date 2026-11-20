using System.Security.Cryptography.X509Certificates;
using Monstertacle.Hosting.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Worker");
if (settings["Runtime"] != "deterministic" ||
    settings["StateDirectory"] is not { } directory ||
    !Guid.TryParse(settings["TrustScope"], out var trustScope) ||
    settings["BrokerClientCertificateFile"] is not { } certificateFile ||
    !Uri.TryCreate(settings["BrokerUrl"], UriKind.Absolute, out var brokerUrl) ||
    brokerUrl.Scheme != Uri.UriSchemeHttps || brokerUrl.AbsolutePath != "/" ||
    brokerUrl.UserInfo.Length != 0 || brokerUrl.Query.Length != 0 || brokerUrl.Fragment.Length != 0)
{
    throw new InvalidOperationException("Configure the worker runtime, private state and HTTPS broker connection.");
}

var primary = DeterministicProvider.Bind("deterministic-primary");
var secondary = DeterministicProvider.Bind("deterministic-secondary");
var selected = settings["Profile"] switch
{
    "deterministic-primary" => primary,
    "deterministic-secondary" => secondary,
    _ => throw new InvalidOperationException("Configure a registered worker profile.")
};

using var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificateFile, settings["BrokerClientCertificatePassword"]);
using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
handler.ClientCertificates.Add(certificate);
using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = brokerUrl, Timeout = TimeSpan.FromSeconds(15) };
await using var runtime = await WorkerRuntime.OpenAsync(directory, trustScope,
    [primary.Registration, secondary.Registration], [primary.Profile, secondary.Profile], CancellationToken.None);
var ledger = new WorkerLedger(directory);
builder.Services.AddSingleton(new WorkerDispatch(ledger, new WorkerBrokerClient(http), runtime,
    selected.Profile.AgentProfileId, selected.Registration.Provider.Capabilities.SupportsCancellation));
builder.Services.AddHostedService<WorkerPollingService>();
var app = builder.Build();

app.MapGet("/health/live", () => runtime.Completion.IsCompleted ? Results.StatusCode(503) : Results.NoContent());

await app.RunAsync();
