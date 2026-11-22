using Monstertacle.Hosting.ControlPlane;

if (args is ["policy-enrollment"])
{
    return PolicyEnrollment.Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
}

var app = BrokerApplication.Build(WebApplication.CreateBuilder(args));
app.Run();
return 0;
