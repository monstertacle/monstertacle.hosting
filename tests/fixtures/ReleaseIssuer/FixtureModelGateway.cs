using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class FixtureModelGateway
{
    public static void Map(WebApplication app)
    {
        var calls = new ConcurrentDictionary<(string Model, string Task), byte>();
        var credentials = new[] { "primary", "secondary" }.ToDictionary(lane => $"fixture-{lane}",
            lane => "Bearer " + File.ReadAllText($"/run/discovery/model-{lane}").Trim());

        app.MapPost("/v1/chat/completions", (JsonElement request, HttpRequest http) =>
        {
            var model = request.GetProperty("model").GetString();
            if (model is null || !credentials.TryGetValue(model, out var expected) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected),
                    Encoding.UTF8.GetBytes(http.Headers.Authorization.ToString())))
            {
                return Results.Unauthorized();
            }

            var messages = request.GetProperty("messages");
            var instructions = messages[0].GetProperty("content").GetString();
            var task = messages[1].GetProperty("content").GetString();
            if (messages.GetArrayLength() != 2 ||
                messages[0].GetProperty("role").GetString() != "system" ||
                messages[1].GetProperty("role").GetString() != "user" ||
                instructions != $"Use the {model} profile." ||
                task is not ("first model task" or "second model task") ||
                request.GetProperty("max_completion_tokens").GetInt32() != 64 ||
                request.GetProperty("stream").GetBoolean() ||
                request.GetProperty("n").GetInt32() != 1 || !calls.TryAdd((model, task), 0))
            {
                return Results.BadRequest();
            }

            return Results.Json(new
            {
                choices = new[]
                {
                    new { finish_reason = "stop", message = new { role = "assistant", content = $"{model}: {task}" } },
                },
            });
        });
    }
}
