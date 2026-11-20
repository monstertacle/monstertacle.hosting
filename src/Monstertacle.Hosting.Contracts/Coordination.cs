namespace Monstertacle.Hosting.Contracts;

public static class WorkerContract
{
    public const string Route = "/v1/worker";
}

public sealed record ClaimJob(Guid MutationId);

public sealed record JobClaim(Guid JobId, Guid SessionId, Guid Fence, string Lane, string State, bool CancellationRequested);

public sealed record JobDispatch(JobClaim Claim, string Project, string Task);

public sealed record CancelSession(Guid MutationId);

public sealed record JobObservation(string State, string Certainty, bool CancellationUnsupported = false)
{
    public bool IsValid() => (State, Certainty) switch
    {
        ("running", "in-progress") => true,
        ("succeeded", "confirmed-ended") => true,
        ("failed" or "interrupted" or "timed-out" or "cancelled",
            "not-started" or "confirmed-ended" or "unknown") => true,
        _ => false
    };
}
