using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Client;
using Monstertacle.Hosting.Contracts;

namespace Monstertacle.Hosting.Tool;

internal static class Program
{
    private const string Syntax = "monstertacle-hosting submit <mutation-uuid> <task> | status <session-uuid> | lookup <mutation-uuid>";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<int> Main(string[] args) =>
        RunAsync(args, Environment.GetEnvironmentVariable, Console.Out);

    internal static async Task<int> RunAsync(string[] args, Func<string, string?> environment, TextWriter output,
        HttpMessageHandler? handler = null)
    {
        if (args is ["--help"] or ["-h"])
        {
            await output.WriteLineAsync(Syntax);
            return 0;
        }

        try
        {
            var project = Required(environment, "HOSTING_PROJECT");
            var (tool, arguments) = ParseCommand(args, project);
            var resource = HttpsEndpoint(environment, "HOSTING_MCP_URL");
            if (resource.AbsolutePath != "/mcp" || resource.Query.Length != 0)
            {
                throw new ToolException("HOSTING_MCP_URL must identify the HTTPS /mcp resource.");
            }
            var tokenEndpoint = HttpsEndpoint(environment, "HOSTING_TOKEN_URL");
            var audience = Required(environment, "HOSTING_ASSERTION_AUDIENCE");
            var scope = Required(environment, "HOSTING_SCOPE");
            if (scope.Any(char.IsWhiteSpace))
            {
                throw new ToolException("HOSTING_SCOPE must contain a single scope.");
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var cancellationToken = deadline.Token;
            using var network = handler ?? TokenExchange.CreateHandler();
            using var http = TokenExchange.CreateClient(network, disposeHandler: false);
            var assertion = await TokenExchange.GetAssertionAsync(http, environment("ACTIONS_ID_TOKEN_REQUEST_URL"),
                environment("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), audience, cancellationToken);
            var accessToken = await TokenExchange.ExchangeAsync(http, tokenEndpoint, assertion, resource, scope, cancellationToken);
            using var invoker = new HttpMessageInvoker(network, disposeHandler: false);
            using var mcpHttp = TokenExchange.CreateClient(new ResourceBearerHandler(invoker, resource, accessToken));
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = resource, TransportMode = HttpTransportMode.StreamableHttp
            }, mcpHttp), cancellationToken: cancellationToken);
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
            if (result.IsError == true || result.StructuredContent is not { } content ||
                content.Deserialize<SessionStatus>(Json) is not { } status ||
                status.Project != project || status.SessionId == Guid.Empty || status.Jobs is null)
            {
                throw new ToolException("The session operation was denied or returned an invalid response.");
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(status, Json));
            return 0;
        }
        catch (Exception error)
        {
            // Third-party exception text may contain remote bodies, credentials or task text.
            var message = error switch
            {
                ToolException => error.Message,
                OperationCanceledException => "The operation exceeded its deadline.",
                _ => "The Hosting request failed."
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(new { error = message }, Json));
            return 1;
        }
    }

    private static (string Tool, Dictionary<string, object?> Arguments) ParseCommand(string[] args, string project)
    {
        if (args.Length < 2 || !Guid.TryParseExact(args[1], "D", out var id) || id == Guid.Empty)
        {
            throw new ToolException(Syntax);
        }

        return args switch
        {
            ["submit", _, var task] when new StartSession(id, project, task).IsValid() =>
                ("session_start", new() { ["project"] = project, ["mutationId"] = id, ["task"] = task }),
            ["status", _] => ("session_status", new() { ["sessionId"] = id }),
            ["lookup", _] => ("session_lookup", new() { ["project"] = project, ["mutationId"] = id }),
            _ => throw new ToolException(Syntax)
        };
    }

    private static string Required(Func<string, string?> environment, string name) =>
        environment(name) is { Length: > 0 and <= 2048 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ToolException($"Set {name} in the trusted job configuration.");

    private static Uri HttpsEndpoint(Func<string, string?> environment, string name) =>
        Uri.TryCreate(Required(environment, name), UriKind.Absolute, out var endpoint) && TokenExchange.IsHttps(endpoint)
            ? endpoint : throw new ToolException($"{name} requires an explicit HTTPS endpoint.");

    private sealed class ResourceBearerHandler(HttpMessageInvoker http, Uri resource, string token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri != resource.AbsoluteUri)
            {
                throw new ToolException("The request left the configured MCP endpoint.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http.SendAsync(request, cancellationToken);
        }
    }
}
