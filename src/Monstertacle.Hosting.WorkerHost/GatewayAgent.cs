using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Monstertacle.Multisession.AgentFramework;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class GatewayAgent(AIAgent inner) : DelegatingAIAgent(inner)
{
    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
        AgentRunOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            return await base.RunCoreAsync(messages, session, options, cancellationToken);
        }
        catch (GatewayRejection rejection)
        {
            return new AgentResponse
            {
                RawRepresentation = new FrameworkExecutionOutcome
                {
                    State = RequestState.Failed,
                    Certainty = rejection.Certainty,
                    ErrorCode = ErrorCode.ExecutionFailed,
                },
            };
        }
    }
}
