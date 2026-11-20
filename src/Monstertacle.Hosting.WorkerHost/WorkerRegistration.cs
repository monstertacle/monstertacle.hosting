using Microsoft.Agents.AI;
using Monstertacle.Multisession.AgentFramework;
using Monstertacle.Multisession.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

/// <summary>A worker profile and provider selected by the deployment composition root.</summary>
public sealed class WorkerRegistration
{
    internal WorkerRegistration(ProviderRegistration provider, AgentProfile profile)
    {
        Provider = provider;
        Profile = profile;
    }

    internal ProviderRegistration Provider { get; }
    internal AgentProfile Profile { get; }

    /// <summary>Registers an AgentFramework agent factory. The deployment owns the agent and its credentials.</summary>
    /// <param name="profileId">Stable identity selected through the worker's profile setting.</param>
    /// <param name="providerId">Stable connection identity.</param>
    /// <param name="modelId">Opaque, non-secret model identity.</param>
    /// <param name="settingsRevision">Stable revision of the deployment's profile settings.</param>
    /// <param name="options">The adapter contract, including a stable adapter ID and execution kind.</param>
    /// <param name="agentFactory">A deployment-owned factory with in-process access to the configured agent client.</param>
    public static WorkerRegistration AgentFramework(string profileId, string providerId, string modelId,
        string settingsRevision, FrameworkAgentOptions options, Func<ProviderSessionContext, AIAgent> agentFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsRevision);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentFactory);
        if (options.RequiresWorkspace)
        {
            throw new ArgumentException("The hosted worker contract does not provision an agent workspace.", nameof(options));
        }

        var provider = new FrameworkSessionProvider(options, agentFactory);
        var profile = new AgentProfile(profileId, providerId, modelId, options.ExecutionKind,
            new(settingsRevision, new Dictionary<string, string>()));
        return new(new ProviderRegistration(providerId, options.AdapterId, 1, provider), profile);
    }
}
