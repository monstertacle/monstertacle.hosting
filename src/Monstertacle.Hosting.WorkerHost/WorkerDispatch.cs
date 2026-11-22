using System.Security.Cryptography;
using System.Text;
using Monstertacle.Hosting.Contracts;
using Monstertacle.Multisession;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class WorkerDispatch(
    WorkerLedger ledger, WorkerBrokerClient broker, SessionRuntime runtime, string profile, bool supportsCancellation,
    ILogger<WorkerDispatch>? logger = null)
{
    private Guid? warnedFence;

    public async Task PollAsync(CancellationToken token)
    {
        var (mutation, claim) = ledger.Read();
        if (claim is null)
        {
            claim = await broker.ClaimAsync(mutation, token);
            if (claim is null)
            {
                return;
            }

            ledger.Attach(mutation, claim);
        }

        var current = await broker.StatusAsync(claim, token);
        ValidateClaim(claim, current);
        var request = await runtime.Requests.GetRequestAsync(claim.Fence, token);
        if (request is null)
        {
            if (current.CancellationRequested)
            {
                if (current.State != "cancelled")
                {
                    await broker.CompleteAsync(claim,
                        new(ResultContract.Version, claim.SessionId, claim.JobId, new("cancelled", "not-started"), []), token);
                }

                ledger.Advance(mutation);
                return;
            }

            var dispatch = await broker.AuthorizeAsync(claim, token);
            if (dispatch is null)
            {
                if (current.State is not ("claimed" or "dispatch-authorized"))
                {
                    throw new InvalidOperationException("Broker execution has no matching local runtime request.");
                }

                return;
            }

            ValidateClaim(claim, dispatch.Claim);
            var session = await runtime.Sessions.GetSessionAsync(claim.JobId, token);
            if (session is null)
            {
                await runtime.Sessions.CreateSessionAsync(claim.JobId, profile, new(null, null), token);
            }
            else if (session.AgentProfileId != profile)
            {
                throw new InvalidOperationException("The existing runtime session uses a different worker profile.");
            }

            current = await broker.StatusAsync(claim, token);
            ValidateClaim(claim, current);
            if (!current.CancellationRequested)
            {
                await runtime.Requests.SubmitAsync(claim.Fence,
                    new(claim.JobId, dispatch.Task, TimeSpan.FromMinutes(2), null, false), token);
            }

            return;
        }

        if (request.SessionId != claim.JobId)
        {
            throw new InvalidOperationException("The runtime request belongs to another job.");
        }

        var unsupported = current.CancellationRequested && !supportsCancellation && request.StartedUtc is not null;
        if (current.CancellationRequested && request.CancellationRequestedUtc is null &&
            request.State is RequestState.Queued or RequestState.Running)
        {
            await runtime.Requests.CancelRequestAsync(request.RequestId, token);
        }

        if (request.State == RequestState.Queued)
        {
            return;
        }

        var evidence = ToEvidence(request, unsupported);
        if (request.Certainty == ExecutionCertainty.Unknown && warnedFence != claim.Fence)
        {
            logger?.LogWarning("Worker job {JobId} has an unconfirmed outcome. The assignment is retained for reconciliation.", claim.JobId);
            warnedFence = claim.Fence;
        }

        if (request.State != RequestState.Running && request.Certainty != ExecutionCertainty.Unknown)
        {
            await broker.CompleteAsync(claim, CreateCompletion(claim, evidence, request.Result), token);
            ledger.Advance(mutation);
        }
        else
        {
            await broker.ReportAsync(claim, evidence, token);
        }
    }

    private static JobCompletion CreateCompletion(JobClaim claim, JobObservation evidence, ProviderReply? result)
    {
        ArtifactUpload[] artifacts = [];
        if (evidence.Certainty == "confirmed-ended" && result is { Output.Length: > 0 } captured)
        {
            var bytes = Encoding.UTF8.GetBytes(captured.Output);
            artifacts = [new("output", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes)];
        }

        return new(ResultContract.Version, claim.SessionId, claim.JobId, evidence, artifacts);
    }

    private static void ValidateClaim(JobClaim expected, JobClaim actual)
    {
        if (expected.JobId != actual.JobId || expected.SessionId != actual.SessionId ||
            expected.Fence != actual.Fence || expected.Lane != actual.Lane)
        {
            throw new InvalidOperationException("The broker changed the durable claim identity.");
        }
    }

    private static JobObservation ToEvidence(RequestSnapshot request, bool unsupported)
    {
        var state = request.State switch
        {
            RequestState.Running => "running",
            RequestState.Succeeded => "succeeded",
            RequestState.Failed => "failed",
            RequestState.Cancelled => "cancelled",
            RequestState.TimedOut => "timed-out",
            RequestState.Interrupted => "interrupted",
            _ => throw new InvalidOperationException("The runtime returned an unsupported state.")
        };
        var certainty = request.Certainty switch
        {
            ExecutionCertainty.ConfirmedEnded => "confirmed-ended",
            ExecutionCertainty.InProgress => "in-progress",
            ExecutionCertainty.NotStarted => "not-started",
            ExecutionCertainty.Unknown => "unknown",
            _ => throw new InvalidOperationException("The runtime returned unsupported execution evidence.")
        };
        return new(state, certainty, unsupported);
    }
}
