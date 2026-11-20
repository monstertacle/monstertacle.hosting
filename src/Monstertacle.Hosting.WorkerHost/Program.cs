using Monstertacle.Hosting.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["Worker:Runtime"] != "deterministic")
{
    throw new InvalidOperationException("Select the deterministic worker or supply a deployment-owned agent composition.");
}

var primary = DeterministicProvider.Bind("deterministic-primary");
var secondary = DeterministicProvider.Bind("deterministic-secondary");
await WorkerApplication.RunAsync(builder,
    [new WorkerRegistration(primary.Registration, primary.Profile), new WorkerRegistration(secondary.Registration, secondary.Profile)]);
