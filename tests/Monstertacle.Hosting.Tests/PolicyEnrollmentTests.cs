using System.Text;
using Microsoft.Extensions.Configuration;
using Monstertacle.Hosting.ControlPlane;
using Monstertacle.Hosting.Security;

namespace Monstertacle.Hosting.Tests;

public sealed class PolicyEnrollmentTests
{
    private const string Issuer = "https://identity.example.net/realms/hosting";
    private const string BasePolicy = """
        {"version":1,"people":[],"projects":[{"id":"example","lanes":["primary","secondary"]}],
         "workload":{"id":"ci","identity":{"issuer":"https://ci.example.net","subject":"build"},
                     "expiresUtc":"2099-01-01T00:00:00Z","project":"example",
                     "grants":[{"project":"example","lane":"primary","actions":["start"]}]}}
        """;

    [Fact]
    public void GrantsAndRevocationPreserveOtherPeopleAndWorkload()
    {
        var first = Run(BasePolicy, Request("person-one", "opaque:Some id"));
        Assert.Equal(0, first.Status);
        var second = Run(first.Output, Request("person-two", "opaque:other", "read"));
        Assert.Equal(0, second.Status);
        var retry = Run(second.Output, Request("person-one", "opaque:Some id"));
        Assert.Equal(PolicyEnrollment.Unchanged, retry.Status);
        Assert.Empty(retry.Output);

        using var file = new TemporaryPolicy(second.Output);
        var runtime = file.Open();
        Assert.True(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:Some id"))!.Allows("example", ["primary"], "start"));
        Assert.False(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:Some id"))!.Allows("example", ["primary", "secondary"], "start"));
        Assert.False(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:other"))!.Allows("example", ["primary"], "start"));
        Assert.Null(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:some id")));
        Assert.Null(runtime.Resolve(new BrokerPolicy.Identity(Issuer + "/", "opaque:Some id")));
        var workload = runtime.Resolve(new BrokerPolicy.Identity("https://ci.example.net", "build"))!;
        Assert.True(workload.Allows("example", ["primary"], "start"));

        var revoked = Run(second.Output, """
            {"operation":"revoke","id":"person-one","issuer":"https://identity.example.net/realms/hosting","subject":"opaque:Some id"}
            """);
        Assert.Equal(0, revoked.Status);
        file.Write(revoked.Output);
        Assert.Equal("person-one", runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:Some id"))!.Principal);
        Assert.False(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:Some id"))!.Allows("example", ["primary"], "start"));
        Assert.True(runtime.Resolve(new BrokerPolicy.Identity(Issuer, "opaque:other"))!.Allows("example", ["primary"], "read"));
        Assert.Equal(workload.Principal, runtime.Resolve(new BrokerPolicy.Identity("https://ci.example.net", "build"))!.Principal);
    }

    [Fact]
    public void ConflictingOrMalformedInputProducesNoCandidate()
    {
        var policy = Run(BasePolicy, Request("person-one", "opaque:Some id")).Output;
        foreach (var (current, request) in new[]
        {
            (policy, Request("wrong-owner", "opaque:Some id")),
            (policy, Request("person-one", "changed")),
            (policy, Request("person-one", "opaque:Some id", "cancel")),
            (policy, Request("person-two", "new", "unknown-action")),
            (policy, Request("person-two", "new").Replace("\"primary\"", "\"unknown-lane\"")),
            (policy, Request("person-two", "new").Replace("\"example\"", "\"unknown-project\"")),
            (policy, Request("person-two", "new").Replace(Issuer, "https://ci.example.net")),
            (policy, Request("person-two", "new").Replace(Issuer, "https://identity.example.net?")),
            (policy, Request("person-two", "new").Replace("\"enroll\"", "\"enroll\",\"operation\":\"enroll\"")),
            (BasePolicy.Replace("\"version\":1", "\"version\":1,\"version\":1"), Request("person-two", "new")),
            ("{", Request("person-two", "new")),
            (new string(' ', BrokerPolicy.MaximumBytes + 1), Request("person-two", "new"))
        })
        {
            var result = Run(current, request);
            Assert.Equal(2, result.Status);
            Assert.Empty(result.Output);
            Assert.NotEmpty(result.Error);
            Assert.DoesNotContain("opaque:Some id", result.Error);
        }
    }

    private static string Request(string id, string subject, string action = "start") => $$"""
        {"operation":"enroll","id":"{{id}}","issuer":"{{Issuer}}","subject":"{{subject}}",
         "grants":[{"project":"example","lane":"primary","actions":["{{action}}"]}]}
        """;

    private static (int Status, string Output, string Error) Run(string policy, string request)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(request + "\0" + policy));
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var status = PolicyEnrollment.Run(input, output, errors);
        return (status, Encoding.UTF8.GetString(output.ToArray()), errors.ToString());
    }

    private sealed class TemporaryPolicy : IDisposable
    {
        private readonly string path = Path.GetTempFileName();

        public TemporaryPolicy(string policy) => Write(policy);

        public BrokerPolicy Open() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PolicyFile"] = path })
            .Build(), "https://ci.example.net");

        public void Write(string policy) => File.WriteAllText(path, policy);

        public void Dispose() => File.Delete(path);
    }
}
