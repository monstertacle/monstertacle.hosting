using Monstertacle.Hosting.ControlPlane;

var app = BrokerApplication.Build(WebApplication.CreateBuilder(args));
app.Run();
