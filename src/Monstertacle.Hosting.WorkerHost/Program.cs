using Monstertacle.Hosting.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Worker");
if (settings["Runtime"] != "deterministic" ||
    settings["StateDirectory"] is not { } directory ||
    !Guid.TryParse(settings["TrustScope"], out var trustScope))
{
    throw new InvalidOperationException("Configure a worker runtime, state directory and trust scope.");
}

var primary = DeterministicProvider.Bind("deterministic-primary");
var secondary = DeterministicProvider.Bind("deterministic-secondary");
await using var runtime = await WorkerRuntime.OpenAsync(directory, trustScope,
    [primary.Registration, secondary.Registration], [primary.Profile, secondary.Profile], CancellationToken.None);
builder.Services.AddSingleton(runtime);
var app = builder.Build();

app.MapGet("/health/live", () => runtime.Completion.IsCompleted ? Results.StatusCode(503) : Results.NoContent());

await app.RunAsync();
