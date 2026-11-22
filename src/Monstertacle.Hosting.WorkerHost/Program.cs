using Monstertacle.Hosting.WorkerHost;

var builder = WebApplication.CreateBuilder(args);
switch (builder.Configuration["Worker:Runtime"])
{
    case "deterministic":
        var primary = DeterministicProvider.Bind("deterministic-primary");
        var secondary = DeterministicProvider.Bind("deterministic-secondary");
        await WorkerApplication.RunAsync(builder,
            [new WorkerRegistration(primary.Registration, primary.Profile), new WorkerRegistration(secondary.Registration, secondary.Profile)]);
        break;
    case "model-gateway":
        var (model, authorization) = ConfiguredModel.Load(builder.Configuration);
        using (var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(90),
        })
        using (var gateway = new GatewayChatClient(http, model, authorization))
        {
            await WorkerApplication.RunAsync(builder, [model.Register(gateway)]);
        }
        break;
    default:
        throw new InvalidOperationException("Select the deterministic or model-gateway worker runtime.");
}
