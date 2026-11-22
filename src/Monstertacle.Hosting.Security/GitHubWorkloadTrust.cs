using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Monstertacle.Hosting.Security;

internal sealed class GitHubWorkloadTrust
{
    public const string Issuer = "https://token.actions.githubusercontent.com";

    private readonly JsonWebTokenHandler handler = new() { MaximumTokenSizeInBytes = 16 * 1024 };
    private readonly string audience;
    private readonly string repositoryId;
    private readonly string workflowRef;
    private readonly string branchRef;
    private readonly string eventName;
    private readonly string? environment;
    private readonly string? jobWorkflowRef;

    public GitHubWorkloadTrust(string audience, string repositoryId, string workflowRef, string branchRef,
        string eventName, string? environment = null, string? jobWorkflowRef = null)
    {
        if (string.IsNullOrWhiteSpace(audience) ||
            string.IsNullOrEmpty(repositoryId) || !repositoryId.All(char.IsAsciiDigit) ||
            !branchRef.StartsWith("refs/heads/", StringComparison.Ordinal) || branchRef.Length <= "refs/heads/".Length ||
            !workflowRef.Contains("/.github/workflows/", StringComparison.Ordinal) ||
            !workflowRef.EndsWith("@" + branchRef, StringComparison.Ordinal) ||
            eventName is not ("push" or "workflow_dispatch") ||
            (environment is not null && string.IsNullOrWhiteSpace(environment)) ||
            (jobWorkflowRef is not null && !jobWorkflowRef.Contains("/.github/workflows/", StringComparison.Ordinal)))
        {
            throw new ArgumentException("GitHub workload trust requires an explicit repository, workflow, branch, audience and trusted event.");
        }

        this.audience = audience;
        this.repositoryId = repositoryId;
        this.workflowRef = workflowRef;
        this.branchRef = branchRef;
        this.eventName = eventName;
        this.environment = environment;
        this.jobWorkflowRef = jobWorkflowRef;
    }

    public async Task<bool> AcceptsAsync(string assertion, IEnumerable<SecurityKey> issuerKeys)
    {
        var result = await handler.ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = issuerKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            IssuerValidator = (value, _, _) => value == Issuer ? value : throw new SecurityTokenInvalidIssuerException(),
            ValidateAudience = true,
            AudienceValidator = (values, _, _) => values?.Take(2).ToArray() is [var value] && value == audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero
        });
        if (!result.IsValid || result.ClaimsIdentity is not { } claims)
        {
            return false;
        }

        return Matches(claims, "repository_id", repositoryId) &&
               Matches(claims, "workflow_ref", workflowRef) &&
               Matches(claims, "ref", branchRef) &&
               Matches(claims, "event_name", eventName) &&
               Matches(claims, "environment", environment) &&
               (jobWorkflowRef is { } reusable
                   ? Matches(claims, "job_workflow_ref", reusable)
                   : Matches(claims, "job_workflow_ref", null) || Matches(claims, "job_workflow_ref", workflowRef));
    }

    private static bool Matches(ClaimsIdentity claims, string type, string? expected) =>
        claims.FindAll(type).Take(2).ToArray() switch
        {
            [] => expected is null,
            [{ Value: var value }] => expected is not null && value == expected,
            _ => false
        };
}
