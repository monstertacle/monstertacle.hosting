using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Monstertacle.Hosting.WorkerHost;
using Monstertacle.Multisession.Contracts;
using Xunit;

namespace Monstertacle.Hosting.Tests;

public sealed class ConfiguredModelTests
{
    private const string Success = """
        {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"fixture answer"}}]}
        """;

    [Theory]
    [InlineData("Model:Endpoint", "http://gateway.invalid/v1/chat/completions")]
    [InlineData("Model:Endpoint", "https://gateway.invalid/v1/chat/completions?query")]
    [InlineData("Model:Endpoint", "https://gateway.invalid")]
    [InlineData("Model:OutputTokenLimit", "0")]
    [InlineData("Model:BearerFile", "relative")]
    [InlineData("Worker:Profile", "fixture@v2")]
    public void InvalidConfigurationFailsClosed(string key, string value)
    {
        using var settings = new Settings();
        settings.Values[key] = value;
        Assert.Throws<InvalidOperationException>(() => settings.Load());
    }

    [Fact]
    public void MultilineBearerIsRejectedWithoutEchoingIt()
    {
        using var settings = new Settings();
        File.WriteAllText(settings.FileName, settings.Token + "\nanother line");
        var failure = Assert.Throws<InvalidOperationException>(() => settings.Load());
        Assert.DoesNotContain(settings.Token, failure.ToString());
        Assert.DoesNotContain(settings.FileName, failure.ToString());
    }

    [Fact]
    public async Task GatewayReceivesOnlyWorkerSelectedSettings()
    {
        using var settings = new Settings();
        var (model, authorization) = settings.Load();
        using var wire = new TestWire(async (request, token) =>
        {
            Assert.Equal(model.Endpoint, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.True(authorization.Equals(request.Headers.Authorization), "Unexpected gateway authorization.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var json = body.RootElement;
            Assert.Equal(model.Model, json.GetProperty("model").GetString());
            Assert.Equal(64, json.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(json.GetProperty("stream").GetBoolean());
            var messages = json.GetProperty("messages");
            Assert.Equal(model.Instructions, messages[0].GetProperty("content").GetString());
            Assert.Equal("test task", messages[1].GetProperty("content").GetString());
            return Reply(HttpStatusCode.OK, Success);
        });
        using var http = new HttpClient(wire);
        using var gateway = new GatewayChatClient(http, model, authorization);
        var answer = await gateway.GetResponseAsync([new(ChatRole.User, "test task")],
            new() { ModelId = "caller-override", MaxOutputTokens = 900 });
        Assert.Equal("fixture answer", answer.Text);
        Assert.Equal(1, wire.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, Success, RequestState.Succeeded, ExecutionCertainty.ConfirmedEnded)]
    [InlineData(HttpStatusCode.Unauthorized, "", RequestState.Failed, ExecutionCertainty.ConfirmedEnded)]
    [InlineData(HttpStatusCode.Forbidden, "", RequestState.Failed, ExecutionCertainty.ConfirmedEnded)]
    [InlineData(HttpStatusCode.TooManyRequests, "", RequestState.Failed, ExecutionCertainty.ConfirmedEnded)]
    [InlineData(HttpStatusCode.OK, """{"choices":[{"finish_reason":"length"}]}""", RequestState.Failed, ExecutionCertainty.ConfirmedEnded)]
    [InlineData(HttpStatusCode.BadGateway, "", RequestState.Failed, ExecutionCertainty.Unknown)]
    [InlineData(HttpStatusCode.Redirect, "", RequestState.Failed, ExecutionCertainty.Unknown)]
    [InlineData(HttpStatusCode.OK, "malformed", RequestState.Failed, ExecutionCertainty.Unknown)]
    [InlineData(HttpStatusCode.OK, null, RequestState.Failed, ExecutionCertainty.Unknown)]
    public async Task RuntimePersistsOutcomeWithoutResending(HttpStatusCode status, string? body,
        RequestState state, ExecutionCertainty certainty)
    {
        using var settings = new Settings();
        var (model, authorization) = settings.Load();
        using var wire = new TestWire((_, _) => body is null
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic lost response"))
            : Task.FromResult(Reply(status, body)));
        using var http = new HttpClient(wire);
        using var gateway = new GatewayChatClient(http, model, authorization);
        var binding = model.Register(gateway);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var trust = Guid.NewGuid();
        var job = Guid.NewGuid();
        var fence = Guid.NewGuid();
        var directory = Path.Combine(settings.DirectoryName, "runtime");
        await using (var runtime = await WorkerRuntime.OpenAsync(directory, trust, [binding.Provider], [binding.Profile], ct))
        {
            await runtime.Sessions.CreateSessionAsync(job, model.Profile, new(null, null), ct);
            await runtime.Requests.SubmitAsync(fence, new(job, "test task", TimeSpan.FromSeconds(10), null, false), ct);
            while ((await runtime.Requests.GetRequestAsync(fence, ct))?.State is null or RequestState.Running or RequestState.Queued)
            {
                await Task.Delay(10, ct);
            }
        }

        await using var restarted = await WorkerRuntime.OpenAsync(directory, trust, [binding.Provider], [binding.Profile], ct);
        var result = await restarted.Requests.GetRequestAsync(fence, ct);
        Assert.NotNull(result);
        Assert.Equal(state, result.State);
        Assert.Equal(certainty, result.Certainty);
        Assert.Equal(state == RequestState.Succeeded ? "fixture answer" : "", result.Result?.Output);
        Assert.Equal(1, wire.Calls);
    }

    [Fact]
    public async Task OversizedReplyIsRejected()
    {
        using var settings = new Settings();
        var (model, authorization) = settings.Load();
        using var wire = new TestWire((_, _) => Task.FromResult(
            Reply(HttpStatusCode.OK, new string('x', GatewayChatClient.MaximumResponseBytes + 1))));
        using var http = new HttpClient(wire);
        using var gateway = new GatewayChatClient(http, model, authorization);
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.GetResponseAsync([new(ChatRole.User, "test task")]));
        Assert.Equal(1, wire.Calls);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestWire(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return handle(request, cancellationToken);
        }
    }

    private sealed class Settings : IDisposable
    {
        public string DirectoryName { get; } = Directory.CreateTempSubdirectory("hosting-model-tests-").FullName;
        public string FileName => Path.Combine(DirectoryName, "bearer");
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public Dictionary<string, string?> Values { get; }

        public Settings()
        {
            File.WriteAllText(FileName, Token + "\n");
            Values = new()
            {
                ["Worker:Profile"] = "fixture@v1",
                ["Model:ProfileId"] = "fixture",
                ["Model:Revision"] = "v1",
                ["Model:Endpoint"] = "https://gateway.invalid/v1/chat/completions",
                ["Model:ModelId"] = "fixture-model",
                ["Model:Instructions"] = "Give a short response.",
                ["Model:OutputTokenLimit"] = "64",
                ["Model:BearerFile"] = FileName,
            };
        }

        public (ConfiguredModel, System.Net.Http.Headers.AuthenticationHeaderValue) Load() =>
            ConfiguredModel.Load(new ConfigurationBuilder().AddInMemoryCollection(Values).Build());

        public void Dispose() => Directory.Delete(DirectoryName, recursive: true);
    }
}
