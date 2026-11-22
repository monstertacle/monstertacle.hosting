var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
    options.ListenAnyIP(8443, listener => listener.UseHttps("/run/discovery/server.pfx")));

var metadata = File.ReadAllText("/run/discovery/metadata");
var keys = File.ReadAllText("/run/discovery/jwks");

var app = builder.Build();
app.MapGet("/.well-known/openid-configuration", () => Results.Text(metadata, "application/json"));
app.MapGet("/jwks", () => Results.Text(keys, "application/json"));
if (File.Exists("/run/discovery/model-primary"))
{
    FixtureModelGateway.Map(app);
}
await app.RunAsync();
