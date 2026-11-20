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
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Monstertacle.Hosting.Contracts;
using Monstertacle.Hosting.ControlPlane;

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
        private WebApplication? app;
        private int keyRequests;

        public string MetadataIssuer { get; set; } = Issuer;
        public int KeyRequests => Volatile.Read(ref keyRequests);

        public async Task<HttpClient> StartAsync(string subject)
        {
            var certificatePath = Path.Combine(directory.FullName, "server.pfx");
            File.WriteAllBytes(certificatePath, server.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Broker:Authentication:Issuer"] = Issuer,
                ["Broker:Authentication:Audience"] = Audience,
                ["Broker:Authentication:Scope"] = Scope,
                ["Broker:Authentication:EdgeCertificateSha256"] = edge.GetCertHashString(HashAlgorithmName.SHA256),
                ["Broker:DatabaseFile"] = Path.Combine(directory.FullName, "broker.db"),
                ["Broker:PolicyFile"] = PolicyPath,
                ["Kestrel:Endpoints:Fixture:Url"] = "https://127.0.0.1:0",
                ["Kestrel:Certificates:Default:Path"] = certificatePath
            });
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = new DiscoveryHandler(this));
            app = BrokerApplication.Build(builder);
            await app.StartAsync();
            return Connect(Token(subject));
        }

        public async Task StopAsync()
        {
            if (app is { } running)
            {
                app = null;
                await running.DisposeAsync();
            }
        }

        public HttpClient Connect(string? token, bool useEdgeCertificate = true)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => server.Equals(cert)
            };
            if (useEdgeCertificate)
            {
                handler.ClientCertificates.Add(edge);
            }

            var client = new HttpClient(handler) { BaseAddress = new Uri(app!.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
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
                    new { project = "demo", lane, actions = new[] { "start", "read" } })
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
            server.Dispose();
            directory.Delete(recursive: true);
        }

        private static X509Certificate2 CreateCertificate(string name, string usage)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(usage) }, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
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
