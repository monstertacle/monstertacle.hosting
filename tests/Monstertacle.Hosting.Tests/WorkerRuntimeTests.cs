using Monstertacle.Hosting.WorkerHost;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.Tests;

public sealed class WorkerRuntimeTests
{
    [Fact]
    public async Task ReopensBothRegisteredRuntimesAndRecoversTheOriginalRequest()
    {
        var root = Directory.CreateTempSubdirectory();
        var directory = Path.Combine(root.FullName, "runtime");
        var trustScope = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            var bindings = new[] { DeterministicProvider.Bind("first"), DeterministicProvider.Bind("second") };
            var providers = bindings.Select(binding => binding.Registration).ToArray();
            var profiles = bindings.Select(binding => binding.Profile).ToArray();
            var requests = new List<(Guid Session, Guid Request, string Output)>();
            await using (var runtime = await WorkerRuntime.OpenAsync(directory, trustScope, providers, profiles, token))
            {
                foreach (var profile in profiles)
                {
                    var session = await runtime.Sessions.CreateSessionAsync(Guid.NewGuid(), profile.AgentProfileId, new(null, null), token);
                    var receipt = await runtime.Requests.SubmitAsync(Guid.NewGuid(),
                        new(session.SessionId, "fixture", TimeSpan.FromSeconds(10), null, false), token);
                    var completed = await WaitForCompletion(runtime.Requests, receipt.RequestId, token);
                    Assert.Equal(RequestState.Succeeded, completed.State);
                    Assert.Equal(ExecutionCertainty.ConfirmedEnded, completed.Certainty);
                    Assert.Equal(profile.ProviderId + ": fixture", completed.Result?.Output);
                    requests.Add((session.SessionId, receipt.RequestId, completed.Result!.Output));
                }
            }

            await using var reopened = await WorkerRuntime.OpenAsync(directory, trustScope, providers, profiles, token);
            foreach (var item in requests)
            {
                var page = await reopened.Requests.ListRequestsAsync(item.Session, 2, null, token);
                Assert.Null(page.NextCursor);
                var recovered = Assert.Single(page.Items);
                Assert.Equal(item.Request, recovered.RequestId);
                Assert.Equal(RequestState.Succeeded, recovered.State);
                Assert.Equal(item.Output, recovered.Result?.Output);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<RequestSnapshot> WaitForCompletion(IRequests requests, Guid id, CancellationToken token)
    {
        while (true)
        {
            var snapshot = await requests.GetRequestAsync(id, token);
            if (snapshot is not null && snapshot.State is not (RequestState.Queued or RequestState.Running))
            {
                return snapshot;
            }

            await Task.Delay(20, token);
        }
    }
}
