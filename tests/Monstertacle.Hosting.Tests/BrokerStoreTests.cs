using Microsoft.Extensions.Configuration;
using Monstertacle.Hosting.Contracts;
using Monstertacle.Hosting.ControlPlane;
using static Monstertacle.Hosting.Security.BrokerPolicy;

namespace Monstertacle.Hosting.Tests;

public sealed class BrokerStoreTests
{
    [Fact]
    public void ReplayAcrossStoreRestartPreservesIdentitiesAndRequiresCurrentGrants()
    {
        var fixture = Directory.CreateTempSubdirectory();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Broker:DatabaseFile"] = Path.Combine(fixture.FullName, "broker.db") })
                .Build();
            var identity = new Identity("https://identity.example", "subject");
            var person = new Person("operator", [identity], [new("demo", "primary", ["start", "read"])]);
            var access = new Access(person, identity, [new("demo", ["primary"])]);
            var request = new StartSession(Guid.NewGuid(), "demo", "fixture task");

            var created = new SessionStore(configuration, TimeProvider.System).Start(access, request);
            Assert.Equal(SessionStore.AdmissionOutcome.Created, created.Outcome);
            var original = Assert.IsType<SessionStatus>(created.Session);

            var restarted = new SessionStore(configuration, TimeProvider.System);
            var replay = restarted.Start(access, request);
            Assert.Equal(SessionStore.AdmissionOutcome.Replayed, replay.Outcome);
            Assert.Equal(original.SessionId, replay.Session?.SessionId);
            Assert.Equal(original.Jobs.Single(), replay.Session?.Jobs.Single());
            Assert.Equal(original.SessionId, restarted.Lookup(access, request.Project, request.MutationId)?.SessionId);
            Assert.Equal(original.SessionId, restarted.List(access, request.Project, null, 1).Sessions.Single().SessionId);
            Assert.Equal(SessionStore.AdmissionOutcome.Conflict, restarted.Start(access, request with { Task = "different" }).Outcome);

            var revoked = access with { Person = person with { Grants = [] } };
            Assert.Equal(SessionStore.AdmissionOutcome.Denied, restarted.Start(revoked, request).Outcome);
            Assert.Null(restarted.Status(revoked, original.SessionId));
            Assert.Null(restarted.Lookup(revoked, request.Project, request.MutationId));
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }
}
