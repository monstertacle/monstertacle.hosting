using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using ModelContextProtocol;

namespace Monstertacle.Hosting.McpHost;

internal sealed class BrokerSessionClient(IHttpClientFactory clients, IHttpContextAccessor requests)
{
    public async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var context = requests.HttpContext ?? throw new InvalidOperationException("An MCP HTTP request is required.");
        var token = await context.GetTokenAsync(JwtBearerDefaults.AuthenticationScheme, "access_token");
        if (string.IsNullOrEmpty(token))
        {
            throw new McpException("Session access denied.");
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        try
        {
            using var client = clients.CreateClient(McpApplication.BrokerClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpException(response.StatusCode switch
                {
                    HttpStatusCode.BadRequest => "The session request is invalid.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Session access denied.",
                    HttpStatusCode.NotFound => "The requested session is unavailable.",
                    HttpStatusCode.Conflict => "The mutation ID is already bound to a different request.",
                    HttpStatusCode.TooManyRequests => "The project has reached its admission limit.",
                    _ => "The session broker is unavailable. Reuse the same mutation ID when retrying a mutation."
                });
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                ?? throw new McpException("The session broker returned no response.");
        }
        catch (Exception error) when (error is HttpRequestException ||
                                      (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new McpException("The session broker is unavailable. Reuse the same mutation ID when retrying a mutation.");
        }
    }
}
