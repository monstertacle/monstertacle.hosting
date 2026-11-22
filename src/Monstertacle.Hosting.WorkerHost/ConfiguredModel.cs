using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed partial class ConfiguredModel
{
    private ConfiguredModel(string profile, string revision, Uri endpoint, string model, string instructions, int limit)
    {
        Profile = profile;
        Revision = revision;
        Endpoint = endpoint;
        Model = model;
        Instructions = instructions;
        OutputTokenLimit = limit;
    }

    public string Profile { get; }
    public string Revision { get; }
    public Uri Endpoint { get; }
    public string Model { get; }
    public string Instructions { get; }
    public int OutputTokenLimit { get; }

    public static (ConfiguredModel Model, AuthenticationHeaderValue Authorization) Load(IConfiguration configuration)
    {
        var section = configuration.GetSection("Model");
        if (section["ProfileId"] is not { } id || !Identifier().IsMatch(id) ||
            section["Revision"] is not { } revision || !Identifier().IsMatch(revision) ||
            configuration["Worker:Profile"] != $"{id}@{revision}" ||
            !Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0 ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !endpoint.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ||
            section["ModelId"] is not { Length: > 0 and <= 256 } model || model.Any(char.IsWhiteSpace) ||
            section["Instructions"] is not { Length: > 0 and <= 8192 } instructions || string.IsNullOrWhiteSpace(instructions) ||
            !int.TryParse(section["OutputTokenLimit"], NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ||
            limit is < 1 or > 8192 ||
            section["BearerFile"] is not { } bearerFile || !Path.IsPathFullyQualified(bearerFile))
        {
            throw new InvalidOperationException("Configure a versioned worker model profile and HTTPS endpoint.");
        }

        return (new($"{id}@{revision}", revision, endpoint, model, instructions, limit), ReadBearer(bearerFile));
    }

    public WorkerRegistration Register(GatewayChatClient client) =>
        WorkerRegistration.AgentFramework(Profile, "configured-chat", Model, Revision, new()
        {
            AdapterId = "configured-chat-v1",
            ExecutionKind = ExecutionKind.ModelResponse,
            QualifiedOutcomes = true,
        }, _ => new GatewayAgent(new ChatClientAgent(client, new ChatClientAgentOptions { Id = Profile, Name = Profile })));

    private static AuthenticationHeaderValue ReadBearer(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new StreamReader(file);
            var buffer = new char[4097];
            var length = reader.ReadBlock(buffer);
            if (length is > 0 and <= 4096)
            {
                var token = new string(buffer, 0, length).TrimEnd('\r', '\n');
                if (Bearer().IsMatch(token))
                {
                    return new AuthenticationHeaderValue("Bearer", token);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Report configuration failure without private paths or credential data.
        }

        throw new InvalidOperationException("The configured model bearer file must contain a readable bearer credential.");
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\A[A-Za-z0-9._~+/=-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();
}
