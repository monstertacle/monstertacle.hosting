using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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
    public async Task WorkerRecoversLostClaimAndTerminalResponseWithoutAnotherRuntimeRequest()
    {
        using var fixture = new BrokerFixture();
        fixture.SetPolicy("first");
        fixture.RegisterWorker("primary");
        using var caller = await fixture.StartAsync("first");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        using var start = await caller.PostAsJsonAsync(Sessions, new StartSession(Guid.NewGuid(), "demo", "fixture"), ct);
        var admitted = await ReadStatus(start);
        var primary = admitted.Jobs.Single(job => job.Lane == "primary");
        var directory = fixture.WorkerDirectory;
        var trust = Guid.NewGuid();
        var binding = DeterministicProvider.Bind("fixture");
        Guid fence;
        using (var reconnect = fixture.ConnectWorker("primary",
            handler => new LoseResponse(new LoseResponse(handler, "/dispatch"), "/claims")))
        await using (var runtime = await WorkerRuntime.OpenAsync(directory, trust, [binding.Registration], [binding.Profile], ct))
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

        using var resumed = fixture.ConnectWorker("primary", handler => new LoseResponse(handler, "/observation"));
        await using var reopened = await WorkerRuntime.OpenAsync(directory, trust, [binding.Registration], [binding.Profile], ct);
        var recoveredLedger = new WorkerLedger(directory);
        Assert.Equal(fence, recoveredLedger.Read().Claim?.Fence);
        var resumedWorker = new WorkerDispatch(recoveredLedger, new WorkerBrokerClient(resumed), reopened, "fixture", false);
        await Assert.ThrowsAsync<HttpRequestException>(() => resumedWorker.PollAsync(ct));
        Assert.Equal(fence, recoveredLedger.Read().Claim?.Fence);
        await resumedWorker.PollAsync(ct);
        Assert.Null(recoveredLedger.Read().Claim);

        var status = await caller.GetFromJsonAsync<SessionStatus>($"{Sessions}/{admitted.SessionId}", ct);
        var completed = status!.Jobs.Single(job => job.JobId == primary.JobId);
        Assert.Equal("succeeded", completed.State);
        Assert.Equal("confirmed-ended", completed.ExecutionCertainty);
        var requests = await reopened.Requests.ListRequestsAsync(primary.JobId, 2, null, ct);
        Assert.Equal(fence, Assert.Single(requests.Items).RequestId);
        Assert.Null(requests.NextCursor);
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
        using var unlisted = fixture.ConnectMcpHttp(endpoint, "unlisted");
        foreach (var message in new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"fixture","version":"1"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"session_list","arguments":{"project":"demo"}}}"""
        })
        {
            using var denied = await unlisted.PostAsync(endpoint,
                new StringContent(message, System.Text.Encoding.UTF8, "application/json"), ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

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

    private static SessionStatus ReadMcpStatus(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<JsonElement>(result.StructuredContent)
            .Deserialize<SessionStatus>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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

        public async Task<HttpClient> StartAsync(string subject)
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

            builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new DiscoveryHandler(this));
            app = BrokerApplication.Build(builder);
            await app.StartAsync();
            return Connect(Token(subject));
        }

        public async Task<WebApplication> StartMcpAsync()
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
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
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
                    new { project = "demo", lane, actions = new[] { "start", "read", "cancel" } })
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
                if (request.RequestUri?.AbsoluteUri == Issuer + "/.well-known/openid-configuration")
                {
                    return Reply(new { issuer = fixture.MetadataIssuer, jwks_uri = Issuer + "/jwks" });
                }

                Assert.Equal(Issuer + "/jwks", request.RequestUri?.AbsoluteUri);
                Interlocked.Increment(ref fixture.keyRequests);
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
