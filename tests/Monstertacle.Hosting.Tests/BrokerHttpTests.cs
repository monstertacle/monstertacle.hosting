using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using Monstertacle.Hosting.Contracts;
using Monstertacle.Hosting.ControlPlane;
using Monstertacle.Hosting.McpHost;
using Monstertacle.Hosting.WorkerHost;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.Tests;

public sealed class BrokerHttpTests
{
    private const string Sessions = SessionContract.Route;
    private const string Issuer = "https://identity.example";
    private const string Audience = "https://hosting.example/mcp";
    private const string Scope = "sessions";
    private const string WorkloadIssuer = "https://workload.example";
    private const string WorkloadScope = "ci-sessions";
    private const string WorkloadSubject = "fixture-ci";

    [Fact]
    public async Task WorkloadCanRecoverOnlyItsAdmissionAndRevocationDeniesReplayAndDispatch()
    {
        using var fixture = new BrokerFixture();
        fixture.SetWorkloadPolicy(DateTimeOffset.UtcNow.AddMinutes(10));
        fixture.RegisterWorker("primary");
        using var human = await fixture.StartAsync(WorkloadSubject, workload: true);
        await using var host = await fixture.StartMcpAsync(workload: true);
        var endpoint = new Uri(new Uri(host.Urls.Single()), "/mcp");
        using var http = fixture.ConnectMcpToken(endpoint, fixture.WorkloadToken());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        await using var client = await ConnectMcp(endpoint, http, ct);
        var deniedId = Guid.NewGuid();
        var denied = new Dictionary<string, object?> { ["mutationId"] = deniedId, ["project"] = "other", ["task"] = "fixture" };
        Assert.True((await client.CallToolAsync("session_start", denied, cancellationToken: ct)).IsError);
        Assert.False(fixture.HasAdmission(deniedId));

        var mutation = Guid.NewGuid();
        var start = new Dictionary<string, object?> { ["mutationId"] = mutation, ["project"] = "demo", ["task"] = "fixture" };
        var session = ReadMcpStatus(await client.CallToolAsync("session_start", start, cancellationToken: ct));
        var lookup = new Dictionary<string, object?> { ["mutationId"] = mutation, ["project"] = "demo" };
        Assert.Equal(session.SessionId, ReadMcpStatus(await client.CallToolAsync("session_lookup", lookup, cancellationToken: ct)).SessionId);
        using var humanRead = await human.GetAsync($"{Sessions}/{session.SessionId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, humanRead.StatusCode);

        using var worker = fixture.ConnectWorker("primary");
        var claim = await Claim(worker, new ClaimJob(Guid.NewGuid()));
        var dispatch = $"{WorkerJobPath(claim)}/dispatch";
        fixture.SetWorkloadPolicy(DateTimeOffset.UtcNow.AddMinutes(-1));
        using var expired = await worker.PostAsync(dispatch, null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.True((await client.CallToolAsync("session_start", start, cancellationToken: ct)).IsError);
        fixture.SetWorkloadPolicy(null);
        using var revoked = await worker.PostAsync(dispatch, null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.True((await client.CallToolAsync("session_lookup", lookup, cancellationToken: ct)).IsError);
    }

    [Fact]
    public async Task ConcurrentStartsAndReconnectHaveOneDurableAdmission()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first", "second");
        var start = new StartSession(Guid.NewGuid(), "demo", "fixture");
        SessionStatus admitted;
        using (var client = await fixture.StartAsync("first"))
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync(Sessions, start)));
            try
            {
                Assert.Single(attempts, response => response.StatusCode == HttpStatusCode.Created);
                Assert.Equal(7, attempts.Count(response => response.StatusCode == HttpStatusCode.OK));
                var receipts = await Task.WhenAll(attempts.Select(ReadStatus));
                admitted = receipts[0];
                Assert.All(receipts, receipt =>
                {
                    Assert.Equal(admitted.SessionId, receipt.SessionId);
                    Assert.Equal(admitted.Jobs.ToArray(), receipt.Jobs.ToArray());
                });
            }
            finally
            {
                foreach (var response in attempts)
                {
                    response.Dispose();
                }
            }
        }

        await fixture.StopAsync();
        using var reconnected = await fixture.StartAsync("first");
        using var status = await reconnected.GetAsync($"{Sessions}/{admitted.SessionId}");
        using var lookup = await reconnected.GetAsync($"{Sessions}/by-mutation/demo/{start.MutationId}");
        using var replay = await reconnected.PostAsJsonAsync(Sessions, start);
        using var conflict = await reconnected.PostAsJsonAsync(Sessions, start with { Task = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        foreach (var response in new[] { status, lookup, replay })
        {
            var recovered = await ReadStatus(response);
            Assert.Equal(admitted.SessionId, recovered.SessionId);
            Assert.Equal(admitted.Jobs.ToArray(), recovered.Jobs.ToArray());
        }

        var page = await reconnected.GetFromJsonAsync<SessionPage>($"{Sessions}?project=demo&limit=1");
        Assert.Equal(admitted.SessionId, Assert.Single(page!.Sessions).SessionId);
        Assert.Null(page.NextAfter);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task ValidUnlistedSubjectAndRevokedSubjectAreDeniedByBroker()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first", "second");
        using var first = await fixture.StartAsync("first");
        using var second = fixture.Connect(fixture.Token("second"));
        using var unlisted = fixture.Connect(fixture.Token("unlisted"));
        var request = new StartSession(Guid.NewGuid(), "demo", "fixture");
        using var admission = await first.PostAsJsonAsync(Sessions, request);
        var original = await ReadStatus(admission);
        using var secondAdmission = await second.PostAsJsonAsync(Sessions, request);
        var separate = await ReadStatus(secondAdmission);
        Assert.NotEqual(original.SessionId, separate.SessionId);

        var status = $"{Sessions}/{original.SessionId}";
        var lookup = $"{Sessions}/by-mutation/demo/{request.MutationId}";
        using var isolated = await second.GetAsync(status);
        Assert.Equal(HttpStatusCode.NotFound, isolated.StatusCode);
        await AssertDenied(unlisted, request, status, lookup);

        fixture.SetPolicyWithLanes(["first", "second"], ["primary"]);
        using var revokedReplay = await first.PostAsJsonAsync(Sessions, request);
        using var revokedStatus = await first.GetAsync(status);
        using var revokedLookup = await first.GetAsync(lookup);
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedStatus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedLookup.StatusCode);
        var revokedList = await first.GetFromJsonAsync<SessionPage>($"{Sessions}?project=demo&limit=1");
        Assert.Empty(revokedList!.Sessions);
        Assert.Null(revokedList.NextAfter);

        fixture.SetPolicy("second");
        await AssertDenied(first, request, status, lookup);
        using var stillAllowed = await second.GetAsync($"{Sessions}/{separate.SessionId}");
        Assert.Equal(HttpStatusCode.OK, stillAllowed.StatusCode);

        foreach (var policy in new[] { null, "", "{", """{"version":1,"projects":[],"people":[]}""" })
        {
            fixture.SetRawPolicy(policy);
            await AssertDenied(second, request, status, lookup);
        }

        await fixture.StopAsync();
    }

    [Fact]
    public async Task BrokerRequiresExactTokenAndDiscoveryIssuerAndThePinnedClientCertificate()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first");
        using var valid = await fixture.StartAsync("first");
        using var allowed = await valid.GetAsync($"{Sessions}?project=demo");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var otherKey = RSA.Create(2048);
        var invalidTokens = new[]
        {
            null,
            fixture.Token("first", issuer: "https://other-issuer.example"),
            fixture.Token("first", audience: Audience + "/"),
            fixture.Token("first", scope: "different"),
            fixture.Token("first", expired: true),
            fixture.Token("first", signingKey: new RsaSecurityKey(otherKey)),
            fixture.Token("first", algorithm: SecurityAlgorithms.RsaSha384)
        };
        foreach (var token in invalidTokens)
        {
            using var client = fixture.Connect(token);
            using var response = await client.GetAsync($"{Sessions}?project=demo");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using var unpinned = fixture.Connect(fixture.Token("first"), useEdgeCertificate: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => unpinned.GetAsync($"{Sessions}?project=demo"));

        fixture.MetadataIssuer = "https://other-issuer.example";
        fixture.AdvanceDiscoveryClock();
        using var wrongDiscovery = await valid.GetAsync($"{Sessions}?project=demo");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongDiscovery.StatusCode);
        Assert.Equal(1, fixture.KeyRequests);
        await fixture.StopAsync();
    }

    [Fact]
    public async Task WorkerClaimsAreLaneBoundAndCancellationFencesDispatchAcrossRestart()
    {
        using var fixture = new BrokerFixture();
        fixture.RegisterWorker("primary");
        fixture.RegisterWorker("secondary");
        fixture.RegisterWorker("third");
        fixture.SetPolicy("first");
        using var caller = await fixture.StartAsync("first");
        using var primary = fixture.ConnectWorker("primary");
        using var secondary = fixture.ConnectWorker("secondary");
        using var third = fixture.ConnectWorker("third");
        var request = new StartSession(Guid.NewGuid(), "demo", "fixture");
        using var accepted = await caller.PostAsJsonAsync(Sessions, request);
        var session = await ReadStatus(accepted);
        var claimMutation = new ClaimJob(Guid.NewGuid());
        const string claims = WorkerContract.Route + "/claims";

        using var edgeClaim = await caller.PostAsJsonAsync(claims, claimMutation);
        using var workerRead = await primary.GetAsync($"{Sessions}/{session.SessionId}");
        using var unrelatedLane = await third.PostAsJsonAsync(claims, claimMutation);
        Assert.Equal(HttpStatusCode.Forbidden, edgeClaim.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, workerRead.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unrelatedLane.StatusCode);

        var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Claim(primary, claimMutation)));
        var first = parallel[0];
        Assert.All(parallel, claim => Assert.Equal(first, claim));
        Assert.Equal(session.Jobs.Single(job => job.Lane == "primary").JobId, first.JobId);
        var second = await Claim(secondary, claimMutation);
        Assert.Equal(session.Jobs.Single(job => job.Lane == "secondary").JobId, second.JobId);
        var firstPath = WorkerJobPath(first);
        var secondPath = WorkerJobPath(second);
        using var crossLane = await secondary.PostAsync($"{firstPath}/dispatch", null);
        Assert.Equal(HttpStatusCode.NotFound, crossLane.StatusCode);
        await fixture.StopAsync();

        using var reconnected = await fixture.StartAsync("first");
        using var primaryRestart = fixture.ConnectWorker("primary");
        using var secondaryRestart = fixture.ConnectWorker("secondary");
        Assert.Equal(first, await Claim(primaryRestart, claimMutation));
        using var dispatch = await primaryRestart.PostAsync($"{firstPath}/dispatch", null);
        var grant = await dispatch.Content.ReadFromJsonAsync<JobDispatch>();
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        Assert.Equal(first.Fence, grant?.Claim.Fence);
        Assert.Equal(request.Task, grant?.Task);
        using var repeatDispatch = await primaryRestart.PostAsync($"{firstPath}/dispatch", null);
        Assert.Equal(grant, await repeatDispatch.Content.ReadFromJsonAsync<JobDispatch>());

        fixture.SetPolicy("second");
        using var revokedDispatch = await secondaryRestart.PostAsync($"{secondPath}/dispatch", null);
        using var revokedReplay = await primaryRestart.PostAsync($"{firstPath}/dispatch", null);
        Assert.Equal(HttpStatusCode.Forbidden, revokedDispatch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revokedReplay.StatusCode);
        fixture.SetPolicy("first");

        var cancel = new CancelSession(Guid.NewGuid());
        var cancelPath = $"{Sessions}/{session.SessionId}/cancel";
        using var cancelled = await reconnected.PostAsJsonAsync(cancelPath, cancel);
        using var cancelReplay = await reconnected.PostAsJsonAsync(cancelPath, cancel);
        var result = await ReadStatus(cancelled);
        Assert.Equal(result.Jobs.ToArray(), (await ReadStatus(cancelReplay)).Jobs.ToArray());
        Assert.Equal("dispatch-authorized", result.Jobs.Single(job => job.Lane == "primary").State);
        Assert.Equal("cancelled", result.Jobs.Single(job => job.Lane == "secondary").State);
        Assert.All(result.Jobs, job => Assert.True(job.CancellationRequested));
        using var cannotDispatch = await secondaryRestart.PostAsync($"{secondPath}/dispatch", null);
        Assert.Equal(HttpStatusCode.Conflict, cannotDispatch.StatusCode);
        var observed = await primaryRestart.GetFromJsonAsync<JobClaim>(firstPath);
        Assert.True(observed!.CancellationRequested);
        var running = new JobObservation("running", "in-progress", true);
        using var reportedRunning = await primaryRestart.PostAsJsonAsync($"{firstPath}/observation", running);
        Assert.Equal(HttpStatusCode.NoContent, reportedRunning.StatusCode);
        var unresolved = new JobObservation("interrupted", "unknown", true);
        using var reportedUncertainty = await primaryRestart.PostAsJsonAsync($"{firstPath}/observation", unresolved);
        Assert.Equal(HttpStatusCode.NoContent, reportedUncertainty.StatusCode);
        using var invalidRegression = await primaryRestart.PostAsJsonAsync($"{firstPath}/observation", running);
        Assert.Equal(HttpStatusCode.Conflict, invalidRegression.StatusCode);
        var unresolvedStatus = await reconnected.GetFromJsonAsync<SessionStatus>($"{Sessions}/{session.SessionId}");
        var active = unresolvedStatus!.Jobs.Single(job => job.JobId == first.JobId);
        Assert.True(active.CancellationUnsupported);
        Assert.Equal("unknown", active.ExecutionCertainty);
        await fixture.StopAsync();
    }

    private static async Task<JobClaim> Claim(HttpClient client, ClaimJob mutation)
    {
        using var response = await client.PostAsJsonAsync(WorkerContract.Route + "/claims", mutation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<JobClaim>(await response.Content.ReadFromJsonAsync<JobClaim>());
    }

    private static string WorkerJobPath(JobClaim claim) =>
        $"{WorkerContract.Route}/jobs/{claim.JobId}/claims/{claim.Fence}";

    [Fact]
    public async Task TwoWorkersCompleteAndRecoverLostResponsesWithoutDuplicateExecution()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first");
        fixture.RegisterWorker("primary");
        fixture.RegisterWorker("secondary");
        using var caller = await fixture.StartAsync("first");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        using var start = await caller.PostAsJsonAsync(Sessions, new StartSession(Guid.NewGuid(), "demo", "fixture"), ct);
        var admitted = await ReadStatus(start);
        var primary = admitted.Jobs.Single(job => job.Lane == "primary");
        var directory = fixture.WorkerDirectory;
        var trust = Guid.NewGuid();
        using var chat = new FixtureChatClient();
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { Id = "fixture-agent", Name = "fixture" });
        var registration = WorkerRegistration.AgentFramework("fixture", "fixture-provider", "fixture-model", "1",
            new() { AdapterId = "fixture-adapter" }, _ => agent);
        var binding = WorkerApplication.SelectRegistration("fixture", [registration]);
        Assert.Same(registration, binding);
        Assert.Throws<InvalidOperationException>(() => WorkerApplication.SelectRegistration("unregistered", [registration]));
        Guid fence;
        using (var reconnect = fixture.ConnectWorker("primary",
            handler => new LoseResponse(new LoseResponse(handler, "/dispatch"), "/claims")))
        await using (var runtime = await WorkerRuntime.OpenAsync(directory, trust, [binding.Provider], [binding.Profile], ct))
        {
            var ledger = new WorkerLedger(directory);
            var initial = ledger.Read().Mutation;
            var worker = new WorkerDispatch(ledger, new WorkerBrokerClient(reconnect), runtime, "fixture", false);
            await Assert.ThrowsAsync<HttpRequestException>(() => worker.PollAsync(ct));
            Assert.Equal((initial, null), ledger.Read());
            await Assert.ThrowsAsync<HttpRequestException>(() => worker.PollAsync(ct));
            var attached = ledger.Read().Claim!;
            Assert.Equal(primary.JobId, attached.JobId);
            Assert.Null(await runtime.Requests.GetRequestAsync(attached.Fence, ct));
            fence = attached.Fence;
            await worker.PollAsync(ct);

            while ((await runtime.Requests.GetRequestAsync(fence, ct))?.State != RequestState.Succeeded)
            {
                await Task.Delay(20, ct);
            }
        }

        using var resumed = fixture.ConnectWorker("primary", handler => new LoseResponse(handler, "/result"));
        await using var reopened = await WorkerRuntime.OpenAsync(directory, trust, [binding.Provider], [binding.Profile], ct);
        var recoveredLedger = new WorkerLedger(directory);
        Assert.Equal(fence, recoveredLedger.Read().Claim?.Fence);
        var resumedWorker = new WorkerDispatch(recoveredLedger, new WorkerBrokerClient(resumed), reopened, "fixture", false);
        await Assert.ThrowsAsync<HttpRequestException>(() => resumedWorker.PollAsync(ct));
        Assert.Equal(fence, recoveredLedger.Read().Claim?.Fence);
        await resumedWorker.PollAsync(ct);
        Assert.Null(recoveredLedger.Read().Claim);

        using var secondaryClient = fixture.ConnectWorker("secondary");
        var secondaryDirectory = directory + "-secondary";
        await using var secondaryRuntime = await WorkerRuntime.OpenAsync(secondaryDirectory, Guid.NewGuid(),
            [binding.Provider], [binding.Profile], ct);
        var secondaryLedger = new WorkerLedger(secondaryDirectory);
        var secondaryWorker = new WorkerDispatch(secondaryLedger, new WorkerBrokerClient(secondaryClient),
            secondaryRuntime, "fixture", false);
        await secondaryWorker.PollAsync(ct);
        var secondaryClaim = secondaryLedger.Read().Claim!;
        Assert.Equal(admitted.Jobs.Single(job => job.Lane == "secondary").JobId, secondaryClaim.JobId);
        while (secondaryLedger.Read().Claim is not null)
        {
            await secondaryWorker.PollAsync(ct);
            await Task.Delay(20, ct);
        }

        var status = await caller.GetFromJsonAsync<SessionStatus>($"{Sessions}/{admitted.SessionId}", ct);
        Assert.All(status!.Jobs, job => Assert.Equal("succeeded", job.State));
        var completed = status!.Jobs.Single(job => job.JobId == primary.JobId);
        Assert.Equal("succeeded", completed.State);
        Assert.Equal("confirmed-ended", completed.ExecutionCertainty);
        var requests = await reopened.Requests.ListRequestsAsync(primary.JobId, 2, null, ct);
        var receipt = Assert.Single(requests.Items);
        Assert.Equal(fence, receipt.RequestId);
        Assert.Equal("fixture output", receipt.Result?.Output);
        Assert.Equal(2, chat.Calls);
        Assert.Null(requests.NextCursor);

        var evidence = new JobObservation("succeeded", "confirmed-ended");
        var upload = Output("fixture output");
        var completion = new JobCompletion(ResultContract.Version, admitted.SessionId, primary.JobId, evidence, [upload]);
        var resultUrl = $"{WorkerContract.Route}/jobs/{primary.JobId}/claims/{fence}/result";
        using var replay = await resumed.PostAsJsonAsync(resultUrl, completion, ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var verified = await replay.Content.ReadFromJsonAsync<VerifiedJobResult>(ct);
        Assert.Equal(upload.Sha256, Assert.Single(verified!.Manifest.Artifacts).Sha256);
        Assert.Equal(64, verified.Sha256.Length);

        using var tampered = await resumed.PostAsJsonAsync(resultUrl,
            completion with { Artifacts = [upload with { Sha256 = new string('0', 64) }] }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        using var changed = await resumed.PostAsJsonAsync(resultUrl,
            completion with { Artifacts = [Output("different output")] }, ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var wrongLane = await secondaryClient.PostAsJsonAsync(resultUrl, completion, ct);
        Assert.Equal(HttpStatusCode.NotFound, wrongLane.StatusCode);
    }

    private static ArtifactUpload Output(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new("output", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    [Fact]
    public async Task ResultAccessVerifiesPartialResultsArtifactsAndRevocationAcrossHttpsAndMcp()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first", "second");
        fixture.RegisterWorker("primary");
        fixture.RegisterWorker("secondary");
        using var caller = await fixture.StartAsync("first");
        using var other = fixture.Connect(fixture.Token("second"));
        using var primary = fixture.ConnectWorker("primary");
        using var secondary = fixture.ConnectWorker("secondary");
        await using var edge = await fixture.StartMcpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;

        using var started = await caller.PostAsJsonAsync(Sessions, new StartSession(Guid.NewGuid(), "demo", "fixture"), ct);
        var admitted = await ReadStatus(started);
        var first = await Claim(primary, new(Guid.NewGuid()));
        var second = await Claim(secondary, new(Guid.NewGuid()));
        var firstPath = WorkerJobPath(first);
        var secondPath = WorkerJobPath(second);
        using var firstDispatch = await primary.PostAsync($"{firstPath}/dispatch", null, ct);
        using var secondDispatch = await secondary.PostAsync($"{secondPath}/dispatch", null, ct);
        firstDispatch.EnsureSuccessStatusCode();
        secondDispatch.EnsureSuccessStatusCode();

        var artifact = Output("verified answer");
        using var firstStored = await primary.PostAsJsonAsync($"{firstPath}/result",
            new JobCompletion(ResultContract.Version, admitted.SessionId, first.JobId,
                new("succeeded", "confirmed-ended"), [artifact]), ct);
        firstStored.EnsureSuccessStatusCode();
        var resultPath = $"{Sessions}/{admitted.SessionId}/result";
        var artifactPath = $"{Sessions}/{admitted.SessionId}/jobs/{first.JobId}/artifacts/{artifact.Name}";
        var partial = (await caller.GetFromJsonAsync<VerifiedSessionResult>(resultPath, ct))!;
        Assert.False(partial.Manifest.Complete);
        Assert.Equal("dispatch-authorized", partial.Manifest.Jobs.Single(job => job.Status.JobId == second.JobId).Status.State);
        var acceptance = new AcceptResult(Guid.NewGuid(), partial.Sha256);
        using var partialAcceptance = await caller.PostAsJsonAsync($"{resultPath}/accept", acceptance, ct);
        Assert.Equal(HttpStatusCode.Conflict, partialAcceptance.StatusCode);

        using var secondStored = await secondary.PostAsJsonAsync($"{secondPath}/result",
            new JobCompletion(ResultContract.Version, admitted.SessionId, second.JobId, new("failed", "confirmed-ended"), []), ct);
        secondStored.EnsureSuccessStatusCode();
        var complete = (await caller.GetFromJsonAsync<VerifiedSessionResult>(resultPath, ct))!;
        Assert.True(complete.Manifest.Complete);
        Assert.NotEqual(partial.Sha256, complete.Sha256);
        Assert.Equal("failed", complete.Manifest.Jobs.Single(job => job.Status.JobId == second.JobId).Status.State);

        var content = (await caller.GetFromJsonAsync<ArtifactContent>(artifactPath, ct))!;
        Assert.Equal(artifact.Content, content.Content);
        Assert.Equal(content.Artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(content.Content)));
        using var crossOwner = await other.GetAsync(artifactPath, ct);
        Assert.Equal(HttpStatusCode.NotFound, crossOwner.StatusCode);

        var endpoint = new Uri(new Uri(edge.Urls.Single()), "/mcp");
        using var http = fixture.ConnectMcpHttp(endpoint, "first");
        await using var mcp = await ConnectMcp(endpoint, http, ct);
        var args = new Dictionary<string, object?> { ["sessionId"] = admitted.SessionId };
        var mcpResult = ReadMcp<VerifiedSessionResult>(await mcp.CallToolAsync("session_result", args, cancellationToken: ct));
        Assert.Equal(complete.Sha256, mcpResult.Sha256);
        var artifactArgs = new Dictionary<string, object?>(args) { ["jobId"] = first.JobId, ["name"] = artifact.Name };
        var mcpArtifact = ReadMcp<ArtifactContent>(await mcp.CallToolAsync("session_artifact", artifactArgs, cancellationToken: ct));
        Assert.Equal(artifact.Content, mcpArtifact.Content);

        acceptance = acceptance with { Sha256 = complete.Sha256 };
        var acceptArgs = new Dictionary<string, object?>(args) { ["mutationId"] = acceptance.MutationId, ["sha256"] = acceptance.Sha256 };
        var receipt = ReadMcp<ResultAcceptance>(await mcp.CallToolAsync("session_accept_result", acceptArgs, cancellationToken: ct));
        using var identical = await caller.PostAsJsonAsync($"{resultPath}/accept", acceptance, ct);
        Assert.Equal(receipt, await identical.Content.ReadFromJsonAsync<ResultAcceptance>(ct));
        using var changed = await caller.PostAsJsonAsync($"{resultPath}/accept", acceptance with { Sha256 = partial.Sha256 }, ct);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        fixture.SetPolicyWithLanes(["first", "second"], ["secondary"]);
        using var revoked = await caller.GetAsync(artifactPath, ct);
        using var revokedReplay = await caller.PostAsJsonAsync($"{resultPath}/accept", acceptance, ct);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revokedReplay.StatusCode);
        Assert.True((await mcp.CallToolAsync("session_result", args, cancellationToken: ct)).IsError);
        Assert.True((await mcp.CallToolAsync("session_artifact", artifactArgs, cancellationToken: ct)).IsError);
        Assert.True((await mcp.CallToolAsync("session_accept_result", acceptArgs, cancellationToken: ct)).IsError);

        fixture.SetPolicy("first", "second");
        fixture.CorruptArtifact(first.JobId);
        using var corruptRead = await caller.GetAsync(artifactPath, ct);
        using var corruptReplay = await caller.PostAsJsonAsync($"{resultPath}/accept", acceptance, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, corruptRead.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, corruptReplay.StatusCode);
    }

    private sealed class FixtureChatClient : IChatClient
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains(messages, message => message.Role == ChatRole.User && message.Text == "fixture");
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixture output"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }

    private sealed class LoseResponse(HttpMessageHandler inner, string pathSuffix) : DelegatingHandler(inner)
    {
        private int dropped;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (response.IsSuccessStatusCode &&
                request.RequestUri?.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal) == true &&
                Interlocked.Exchange(ref dropped, 1) == 0)
            {
                response.Dispose();
                throw new HttpRequestException("Fixture dropped a successful response.");
            }

            return response;
        }
    }

    [Fact]
    public async Task FreshMcpClientRecoversSessionAndBrokerRejectsStaleEdgeGrants()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first", "second");
        using var broker = await fixture.StartAsync("first");
        await using var host = await fixture.StartMcpAsync();
        var endpoint = new Uri(new Uri(host.Urls.Single()), "/mcp");
        using var transport = fixture.ConnectMcpHttp(endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var metadata = await transport.GetFromJsonAsync<JsonElement>("/.well-known/oauth-protected-resource/mcp", ct);
        Assert.Equal(Audience, metadata.GetProperty("resource").GetString());
        Assert.Equal(Issuer, metadata.GetProperty("authorization_servers")[0].GetString());

        using var missing = await transport.PostAsJsonAsync(endpoint, new { }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Contains("resource_metadata=\"https://hosting.example/.well-known/oauth-protected-resource/mcp\"",
            missing.Headers.WwwAuthenticate.ToString());
        var deniedMutation = Guid.NewGuid();
        using var unlisted = fixture.ConnectMcpHttp(endpoint, "unlisted");
        foreach (var message in new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"session_list","arguments":{"project":"demo"}}}""",
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new
            {
                name = "session_start", arguments = new { project = "demo", mutationId = deniedMutation, task = "denied" }
            } })
        })
        {
            using var denied = await unlisted.PostAsync(endpoint,
                new StringContent(message, System.Text.Encoding.UTF8, "application/json"), ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var anonymous = await transport.PostAsync(endpoint,
                new StringContent(message, System.Text.Encoding.UTF8, "application/json"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head })
        {
            using var request = new HttpRequestMessage(method, endpoint);
            using var denied = await transport.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.False(fixture.HasAdmission(deniedMutation));

        var mutation = Guid.NewGuid();
        var arguments = new Dictionary<string, object?> { ["mutationId"] = mutation, ["project"] = "demo", ["task"] = "fixture" };
        SessionStatus original;
        using (var firstHttp = fixture.ConnectMcpHttp(endpoint, "first"))
        await using (var first = await ConnectMcp(endpoint, firstHttp, ct))
        {
            var tools = await first.ListToolsAsync(cancellationToken: ct);
            Assert.Contains(tools, tool => tool.Name == "session_start");
            original = ReadMcpStatus(await first.CallToolAsync("session_start", arguments, cancellationToken: ct));
        }

        var lookup = new Dictionary<string, object?> { ["mutationId"] = mutation, ["project"] = "demo" };
        var status = new Dictionary<string, object?> { ["sessionId"] = original.SessionId };
        using var freshHttp = fixture.ConnectMcpHttp(endpoint, "first");
        await using var fresh = await ConnectMcp(endpoint, freshHttp, ct);
        Assert.Equal(original.SessionId, ReadMcpStatus(await fresh.CallToolAsync("session_lookup", lookup, cancellationToken: ct)).SessionId);
        Assert.Equal(original.Jobs.ToArray(),
            ReadMcpStatus(await fresh.CallToolAsync("session_status", status, cancellationToken: ct)).Jobs.ToArray());
        Assert.Equal(original.SessionId,
            ReadMcpStatus(await fresh.CallToolAsync("session_start", arguments, cancellationToken: ct)).SessionId);

        using var secondHttp = fixture.ConnectMcpHttp(endpoint, "second");
        await using var second = await ConnectMcp(endpoint, secondHttp, ct);
        Assert.True((await second.CallToolAsync("session_status", status, cancellationToken: ct)).IsError);

        fixture.SetPolicy("second");
        Assert.Contains(await fresh.ListToolsAsync(cancellationToken: ct), tool => tool.Name == "session_start");
        Assert.True((await fresh.CallToolAsync("session_start", arguments, cancellationToken: ct)).IsError);
        Assert.True((await fresh.CallToolAsync("session_status", status, cancellationToken: ct)).IsError);
    }

    private static Task<McpClient> ConnectMcp(Uri endpoint, HttpClient client, CancellationToken cancellationToken) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp
        }, client), cancellationToken: cancellationToken);

    private static SessionStatus ReadMcpStatus(ModelContextProtocol.Protocol.CallToolResult result) =>
        ReadMcp<SessionStatus>(result);

    private static T ReadMcp<T>(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<JsonElement>(result.StructuredContent)
            .Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task AssertDenied(HttpClient client, StartSession request, string status, string lookup)
    {
        using var start = await client.PostAsJsonAsync(Sessions, request);
        using var read = await client.GetAsync(status);
        using var recovery = await client.GetAsync(lookup);
        Assert.All(new[] { start, read, recovery }, response =>
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    private static async Task<SessionStatus> ReadStatus(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return Assert.IsType<SessionStatus>(await response.Content.ReadFromJsonAsync<SessionStatus>());
    }

    private sealed class BrokerFixture : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory();
        private readonly X509Certificate2 server = CreateCertificate("server", "1.3.6.1.5.5.7.3.1");
        private readonly X509Certificate2 edge = CreateCertificate("edge", "1.3.6.1.5.5.7.3.2");
        private readonly RSA signing = RSA.Create(2048);
        private readonly DiscoveryClock clock = new();
        private readonly Dictionary<string, X509Certificate2> workers = new(StringComparer.Ordinal);
        private WebApplication? app;
        private int keyRequests;

        public string MetadataIssuer { get; set; } = Issuer;
        public int KeyRequests => Volatile.Read(ref keyRequests);
        public string WorkerDirectory => Path.Combine(directory.FullName, "worker");

        public bool HasAdmission(Guid mutation)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory.FullName, "broker.db"), Pooling = false }.ToString());
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT EXISTS (SELECT 1 FROM sessions WHERE mutation_id = $mutation);";
            query.Parameters.AddWithValue("$mutation", mutation.ToString("D"));
            return (long)query.ExecuteScalar()! != 0;
        }

        public void CorruptArtifact(Guid jobId)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory.FullName, "broker.db"), Pooling = false }.ToString());
            connection.Open();
            using var change = connection.CreateCommand();
            change.CommandText = "UPDATE job_artifacts SET content = zeroblob(bytes) WHERE job_id = $job;";
            change.Parameters.AddWithValue("$job", jobId.ToString("D"));
            Assert.Equal(1, change.ExecuteNonQuery());
        }

        public async Task<HttpClient> StartAsync(string subject, bool workload = false)
        {
            var certificatePath = Path.Combine(directory.FullName, "server.pfx");
            File.WriteAllBytes(certificatePath, server.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var settings = new Dictionary<string, string?>
            {
                ["Broker:Authentication:Issuer"] = Issuer,
                ["Broker:Authentication:Audience"] = Audience,
                ["Broker:Authentication:Scope"] = Scope,
                ["Broker:Authentication:EdgeCertificateSha256"] = edge.GetCertHashString(HashAlgorithmName.SHA256),
                ["Broker:DatabaseFile"] = Path.Combine(directory.FullName, "broker.db"),
                ["Broker:PolicyFile"] = PolicyPath,
                ["Kestrel:Endpoints:Fixture:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Certificates:Default:Path"] = certificatePath
            };
            foreach (var (lane, certificate) in workers)
            {
                settings[$"Broker:Workers:{lane}:Lane"] = lane;
                settings[$"Broker:Workers:{lane}:CertificateSha256"] = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            }

            if (workload)
            {
                settings["Broker:Authentication:Workload:Issuer"] = WorkloadIssuer;
                settings["Broker:Authentication:Workload:Scope"] = WorkloadScope;
            }
            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.ConfigureAll<JwtBearerOptions>(
                options => options.BackchannelHttpHandler = new DiscoveryHandler(this));
            app = BrokerApplication.Build(builder);
            await app.StartAsync();
            return Connect(Token(subject));
        }

        public async Task<WebApplication> StartMcpAsync(bool workload = false)
        {
            var edgePolicy = Path.Combine(directory.FullName, "edge-policy.json");
            var edgeCertificate = Path.Combine(directory.FullName, "edge.pfx");
            File.Copy(PolicyPath, edgePolicy);
            File.WriteAllBytes(edgeCertificate, edge.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Authentication:Issuer"] = Issuer,
                ["Mcp:Authentication:Audience"] = Audience,
                ["Mcp:Authentication:Scope"] = Scope,
                ["Mcp:PolicyFile"] = edgePolicy,
                ["Mcp:BrokerUrl"] = app!.Urls.Single(),
                ["Mcp:BrokerClientCertificateFile"] = edgeCertificate,
                ["Kestrel:Endpoints:Fixture:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Certificates:Default:Path"] = Path.Combine(directory.FullName, "server.pfx")
            });
            if (workload)
            {
                builder.Configuration["Mcp:Authentication:Workload:Issuer"] = WorkloadIssuer;
                builder.Configuration["Mcp:Authentication:Workload:Scope"] = WorkloadScope;
            }
            builder.Services.ConfigureAll<JwtBearerOptions>(
                options => options.BackchannelHttpHandler = new DiscoveryHandler(this));
            builder.Services.PostConfigure<HttpClientFactoryOptions>(McpApplication.BrokerClientName,
                options => options.HttpMessageHandlerBuilderActions.Add(handler =>
                    ((HttpClientHandler)handler.PrimaryHandler).ServerCertificateCustomValidationCallback =
                    (_, cert, _, _) => server.Equals(cert)));
            var host = McpApplication.Build(builder);
            await host.StartAsync();
            return host;
        }

        public HttpClient ConnectMcpHttp(Uri endpoint, string? subject = null) =>
            Connect(endpoint, subject is null ? null : Token(subject), null);

        public HttpClient ConnectMcpToken(Uri endpoint, string token) => Connect(endpoint, token, null);

        public async Task StopAsync()
        {
            if (app is { } running)
            {
                app = null;
                await running.DisposeAsync();
            }
        }

        public void RegisterWorker(string lane) => workers.Add(lane, CreateCertificate(lane, "1.3.6.1.5.5.7.3.2"));

        public HttpClient ConnectWorker(string lane, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null) =>
            Connect(new Uri(app!.Urls.Single()), null, workers[lane], wrap);

        public HttpClient Connect(string? token, bool useEdgeCertificate = true) =>
            Connect(token, useEdgeCertificate ? edge : null);

        private HttpClient Connect(string? token, X509Certificate2? certificate) =>
            Connect(new Uri(app!.Urls.Single()), token, certificate);

        private HttpClient Connect(Uri endpoint, string? token, X509Certificate2? certificate,
            Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => server.Equals(cert)
            };
            if (certificate is not null)
            {
                handler.ClientCertificates.Add(certificate);
            }

            var client = new HttpClient(wrap?.Invoke(handler) ?? handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(15) };
            if (token is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return client;
        }

        public string Token(string subject, string issuer = Issuer, string audience = Audience, string scope = Scope,
            bool expired = false, SecurityKey? signingKey = null, string algorithm = SecurityAlgorithms.RsaSha256)
        {
            signingKey ??= new RsaSecurityKey(signing);
            signingKey.KeyId = "fixture";
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(issuer, audience, [new Claim("sub", subject), new Claim("scope", scope)],
                now.AddMinutes(-10), now.AddMinutes(expired ? -1 : 10), new SigningCredentials(signingKey, algorithm));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public string WorkloadToken()
        {
            var now = DateTime.UtcNow;
            var key = new RsaSecurityKey(signing) { KeyId = "fixture" };
            var token = new JwtSecurityToken(WorkloadIssuer, Audience,
                [new("sub", WorkloadSubject), new("scope", WorkloadScope),
                 new("iat", EpochTime.GetIntDate(now).ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
                now.AddSeconds(-1), now.AddMinutes(2), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            token.Header["typ"] = "at+jwt";
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public void SetWorkloadPolicy(DateTimeOffset? expiresUtc) => SetRawPolicy(JsonSerializer.Serialize(new
        {
            version = 1,
            projects = new[] { new { id = "demo", lanes = new[] { "primary" } }, new { id = "other", lanes = new[] { "primary" } } },
            people = new[] { new { id = WorkloadSubject, identities = new[] { new { issuer = Issuer, subject = WorkloadSubject } }, grants = Array.Empty<object>() } },
            workload = expiresUtc is null ? null : new
            {
                id = WorkloadSubject, identity = new { issuer = WorkloadIssuer, subject = WorkloadSubject },
                expiresUtc, project = "demo", grants = new[] { new { project = "demo", lane = "primary", actions = new[] { "start", "read" } } }
            }
        }));

        private string PolicyPath => Path.Combine(directory.FullName, "policy.json");

        public void SetPolicy(params string[] names) => SetPolicyWithLanes(names, ["primary", "secondary"]);

        public void SetPolicyWithLanes(string[] names, string[] lanes) => SetRawPolicy(JsonSerializer.Serialize(new
        {
            version = 1,
            projects = new[] { new { id = "demo", lanes = new[] { "primary", "secondary" } } },
            people = names.Select(name => new
            {
                id = name,
                identities = new[] { new { issuer = Issuer, subject = name } },
                grants = lanes.Select(lane =>
                    new { project = "demo", lane, actions = new[] { "start", "read", "cancel", "result", "artifact", "accept-result" } })
            })
        }));

        public void SetRawPolicy(string? content)
        {
            if (content is null)
            {
                File.Delete(PolicyPath);
                return;
            }

            File.WriteAllText(PolicyPath + ".new", content);
            File.Move(PolicyPath + ".new", PolicyPath, overwrite: true);
        }

        public void AdvanceDiscoveryClock() => clock.Advance();

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            signing.Dispose();
            edge.Dispose();
            foreach (var certificate in workers.Values)
            {
                certificate.Dispose();
            }
            server.Dispose();
            directory.Delete(recursive: true);
        }

        private static X509Certificate2 CreateCertificate(string name, string usage)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(usage) }, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = generated.Export(X509ContentType.Pfx);
            try
            {
                // Schannel needs the client private key to be accessible through the Windows key store.
                return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }

        private sealed class DiscoveryClock : TimeProvider
        {
            private long extraMinutes;
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddMinutes(Interlocked.Read(ref extraMinutes));
            public void Advance() => Interlocked.Add(ref extraMinutes, 6);
        }

        private sealed class DiscoveryHandler(BrokerFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsoluteUri == WorkloadIssuer + "/.well-known/openid-configuration")
                {
                    return Reply(new { issuer = WorkloadIssuer, jwks_uri = WorkloadIssuer + "/jwks" });
                }

                if (request.RequestUri?.AbsoluteUri == Issuer + "/.well-known/openid-configuration")
                {
                    return Reply(new { issuer = fixture.MetadataIssuer, jwks_uri = Issuer + "/jwks" });
                }

                if (request.RequestUri?.AbsoluteUri != WorkloadIssuer + "/jwks")
                {
                    Assert.Equal(Issuer + "/jwks", request.RequestUri?.AbsoluteUri);
                    Interlocked.Increment(ref fixture.keyRequests);
                }
                var key = fixture.signing.ExportParameters(false);
                return Reply(new
                {
                    keys = new[] { new
                    {
                        kty = "RSA", kid = "fixture", use = "sig", alg = "RS256",
                        n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent)
                    } }
                });
            }

            private static Task<HttpResponseMessage> Reply<T>(T content) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(content) });
        }
    }
}
