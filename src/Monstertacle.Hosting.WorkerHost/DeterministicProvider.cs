using System.Text;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class DeterministicProvider(string id) : ISessionProvider
{
    public static (ProviderRegistration Registration, AgentProfile Profile) Bind(string id)
    {
        var provider = new DeterministicProvider(id);
        return (new(id, id, 1, provider),
            new(id, id, "deterministic", ExecutionKind.ModelResponse, new("1", new Dictionary<string, string>())));
    }

    public ProviderCapabilities Capabilities { get; } = new(true, false, false, false, false);

    public ValueTask<ProviderBinding> OpenAsync(ProviderSessionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Binding(context.SessionId));
    }

    public ValueTask<ProviderBinding> ReopenAsync(ProviderSessionContext context, ProviderBinding binding,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (binding != Binding(context.SessionId))
        {
            throw new InvalidOperationException("The deterministic session binding differs from the stored identity.");
        }

        return ValueTask.FromResult(binding);
    }

    public async ValueTask<ProviderReply> SendAsync(ProviderCall request, ProviderBinding binding,
        Func<ProviderObservation, CancellationToken, ValueTask> observe, CancellationToken cancellationToken)
    {
        await observe(new(ProviderObservationKind.OperationAccepted, request.DispatchId.ToString("D")), cancellationToken);
        var output = id + ": " + request.Input;
        if (Encoding.UTF8.GetByteCount(output) > request.MaxOutputBytes)
        {
            return new(RequestState.Failed, ExecutionCertainty.ConfirmedEnded, "", false, binding,
                new(ErrorCode.OutputLimitExceeded, ExecutionCertainty.ConfirmedEnded));
        }

        return new(RequestState.Succeeded, ExecutionCertainty.ConfirmedEnded, output, true, binding, null);
    }

    public ValueTask<ProviderReply> InspectAsync(ProviderSessionContext context, string operationId,
        CancellationToken cancellationToken) => Unsupported();

    public ValueTask<ProviderReply> CancelAsync(ProviderSessionContext context, string operationId,
        CancellationToken cancellationToken) => Unsupported();

    private ProviderBinding Binding(Guid sessionId) => new(id, 1, sessionId.ToString("D"), null);

    private static ValueTask<ProviderReply> Unsupported() => ValueTask.FromResult(
        new ProviderReply(RequestState.Failed, ExecutionCertainty.Unknown, "", false, null,
            new(ErrorCode.UnsupportedProviderAction, ExecutionCertainty.Unknown)));
}
