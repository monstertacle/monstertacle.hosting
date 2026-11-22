using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Monstertacle.Hosting.Contracts;

namespace Monstertacle.Hosting.McpHost;

internal sealed class SessionTools
{
    private const string Route = SessionContract.Route;

    [McpServerTool(Name = "session_start", UseStructuredContent = true)]
    [Description("Admit a task to a registered project. Preserve the mutation ID across retries and reconnects.")]
    public static Task<SessionStatus> Start(Guid mutationId, string project, string task, BrokerSessionClient broker,
        CancellationToken cancellationToken) =>
        broker.Send<SessionStatus>(HttpMethod.Post, Route, new StartSession(mutationId, project, task), cancellationToken);

    [McpServerTool(Name = "session_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read the current state of a session and its jobs.")]
    public static Task<SessionStatus> Status(Guid sessionId, BrokerSessionClient broker, CancellationToken cancellationToken) =>
        broker.Send<SessionStatus>(HttpMethod.Get, $"{Route}/{sessionId:D}", null, cancellationToken);

    [McpServerTool(Name = "session_lookup", UseStructuredContent = true, ReadOnly = true)]
    [Description("Recover a session by its project and original start mutation ID.")]
    public static Task<SessionStatus> Lookup(Guid mutationId, string project, BrokerSessionClient broker,
        CancellationToken cancellationToken) =>
        broker.Send<SessionStatus>(HttpMethod.Get, $"{Route}/by-mutation/{Uri.EscapeDataString(project)}/{mutationId:D}",
            null, cancellationToken);

    [McpServerTool(Name = "session_list", UseStructuredContent = true, ReadOnly = true)]
    [Description("List sessions in a project. Use the returned nextAfter value to retrieve the next page.")]
    public static Task<SessionPage> List(string project, BrokerSessionClient broker, CancellationToken cancellationToken,
        Guid? after = null, int limit = 20) =>
        broker.Send<SessionPage>(HttpMethod.Get, $"{Route}?project={Uri.EscapeDataString(project)}&limit={limit}" +
            (after is { } cursor ? $"&after={cursor:D}" : ""),
            null, cancellationToken);

    [McpServerTool(Name = "session_cancel", UseStructuredContent = true, Destructive = true)]
    [Description("Request cancellation using a stable mutation ID and return the current job states.")]
    public static Task<SessionStatus> Cancel(Guid sessionId, Guid mutationId, BrokerSessionClient broker,
        CancellationToken cancellationToken) =>
        broker.Send<SessionStatus>(HttpMethod.Post, $"{Route}/{sessionId:D}/cancel", new CancelSession(mutationId),
            cancellationToken);

    [McpServerTool(Name = "session_result", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read a verified result manifest and the state of each child. Partial manifests cannot be accepted.")]
    public static Task<VerifiedSessionResult> Result(Guid sessionId, BrokerSessionClient broker, CancellationToken cancellationToken) =>
        broker.Send<VerifiedSessionResult>(HttpMethod.Get, $"{Route}/{sessionId:D}/result", null, cancellationToken);

    [McpServerTool(Name = "session_artifact", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read a bounded result artifact by session, job and manifest artifact name. Content is base64.")]
    public static Task<ArtifactContent> Artifact(Guid sessionId, Guid jobId, string name, BrokerSessionClient broker,
        CancellationToken cancellationToken)
    {
        if (!ResultContract.IsArtifactName(name))
        {
            throw new McpException("The artifact name is invalid.");
        }

        return broker.Send<ArtifactContent>(HttpMethod.Get, $"{Route}/{sessionId:D}/jobs/{jobId:D}/artifacts/{name}",
            null, cancellationToken);
    }

    [McpServerTool(Name = "session_accept_result", UseStructuredContent = true)]
    [Description("Explicitly accept a complete verified result by its digest and a durable mutation ID.")]
    public static Task<ResultAcceptance> Accept(Guid sessionId, Guid mutationId, string sha256, BrokerSessionClient broker,
        CancellationToken cancellationToken) =>
        broker.Send<ResultAcceptance>(HttpMethod.Post, $"{Route}/{sessionId:D}/result/accept", new AcceptResult(mutationId, sha256),
            cancellationToken);
}
