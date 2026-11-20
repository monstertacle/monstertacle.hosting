namespace Monstertacle.Hosting.Contracts;

public static class WorkerContract
{
    public const string Route = "/v1/worker";
}

public sealed record ClaimJob(Guid MutationId);

public sealed record JobClaim(Guid JobId, Guid SessionId, Guid Fence, string Lane, string State, bool CancellationRequested);

public sealed record JobDispatch(JobClaim Claim, string Project, string Task);

public sealed record CancelSession(Guid MutationId);
