using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class GatewayChatClient(HttpClient http, ConfiguredModel model, AuthenticationHeaderValue authorization) : IChatClient
{
    internal const int MaximumResponseBytes = 256 * 1024;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var prompt = new List<GatewayMessage> { new("system", model.Instructions) };
        foreach (var message in messages)
        {
            if ((message.Role != ChatRole.User && message.Role != ChatRole.Assistant) ||
                message.Contents.Count == 0 || message.Contents.Any(content => content is not TextContent))
            {
                throw new GatewayRejection(ExecutionCertainty.NotStarted);
            }

            prompt.Add(new(message.Role.Value, message.Text));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, model.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = model.Model,
                messages = prompt,
                max_completion_tokens = model.OutputTokenLimit,
                n = 1,
                stream = false,
            }),
        };
        request.Headers.Authorization = authorization;
        request.Headers.ConnectionClose = true;

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new GatewayRejection(ExecutionCertainty.ConfirmedEnded);
        }
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            throw new InvalidOperationException("The model gateway response could not be confirmed.");
        }

        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() != 1)
        {
            throw new InvalidOperationException("The model gateway did not return a single choice.");
        }

        var choice = choices[0];
        var finish = choice.GetProperty("finish_reason").GetString();
        if (finish is "length" or "content_filter")
        {
            throw new GatewayRejection(ExecutionCertainty.ConfirmedEnded);
        }

        var reply = choice.GetProperty("message");
        if (finish != "stop" || reply.GetProperty("role").GetString() != "assistant" ||
            HasContent(reply, "tool_calls") || HasContent(reply, "function_call") || HasContent(reply, "refusal") ||
            reply.GetProperty("content").GetString() is not { Length: > 0 } text)
        {
            throw new InvalidOperationException("The model gateway did not confirm a complete text response.");
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.Stop };
    }

    private static bool HasContent(JsonElement message, string property) =>
        message.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    private sealed record GatewayMessage(string Role, string Content);
}

internal sealed class GatewayRejection(ExecutionCertainty certainty) : Exception("The model gateway rejected the request.")
{
    public ExecutionCertainty Certainty { get; } = certainty;
}
