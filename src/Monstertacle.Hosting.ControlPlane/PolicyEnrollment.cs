using System.Text.Json;
using Monstertacle.Hosting.Security;
using static Monstertacle.Hosting.Security.BrokerPolicy;

namespace Monstertacle.Hosting.ControlPlane;

internal static class PolicyEnrollment
{
    private const int MaximumRequestBytes = 64 * 1024;
    internal const int Unchanged = 3;

    internal static int Run(Stream input, Stream output, TextWriter errors)
    {
        try
        {
            var buffer = new byte[MaximumRequestBytes + MaximumBytes + 2];
            var length = input.ReadAtLeast(buffer, buffer.Length, false);
            var message = buffer.AsSpan(0, length);
            var separator = message.IndexOf((byte)0);
            if (length == buffer.Length || separator is <= 0 or > MaximumRequestBytes)
            {
                return Fail(errors, "Invalid enrollment input.");
            }

            var request = ParseJson<Request>(message[..separator]);
            if (!ValidRequest(request))
            {
                return Fail(errors, "Supply an exact HTTPS issuer, opaque subject, principal ID and valid enrollment operation.");
            }

            var policy = Parse(message[(separator + 1)..]);
            if (policy is null)
            {
                return Fail(errors, "The existing version 1 policy is missing or invalid.");
            }

            return Apply(policy, request!, output, errors);
        }
        catch (JsonException)
        {
            return Fail(errors, "The enrollment request or existing policy is invalid JSON.");
        }
        catch (IOException)
        {
            return Fail(errors, "The enrollment input or output could not be accessed.");
        }
    }

    private static bool ValidRequest(Request? request) =>
        request is not null && ValidName(request.Id) && !string.IsNullOrWhiteSpace(request.Subject) &&
        request.Issuer is { } issuer && issuer == issuer.Trim() &&
        !issuer.Any(char.IsWhiteSpace) && Uri.TryCreate(issuer, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0 &&
        !issuer.Contains('#') && !issuer.Contains('?') && !issuer.Contains('\\') &&
        request switch
        {
            { Operation: "enroll", Grants.Length: > 0 } => true,
            { Operation: "revoke", Grants: null } => true,
            _ => false
        };

    private static int Apply(Policy policy, Request request, Stream output, TextWriter errors)
    {
        var identity = new Identity(request.Issuer, request.Subject);
        if (request.Issuer == policy.Workload?.Identity.Issuer || request.Issuer == GitHubWorkloadTrust.Issuer)
        {
            return Fail(errors, "Use the configured human issuer for enrollment.");
        }

        var owner = policy.People.SingleOrDefault(person => person.Identities.Contains(identity));
        var principal = policy.People.SingleOrDefault(person => person.Id == request.Id);
        if (owner != principal || (principal is not null && principal.Identities.Length != 1))
        {
            return Fail(errors, "The exact identity and principal conflict with the existing policy.");
        }

        if (request.Operation == "revoke" && principal is null)
        {
            return Fail(errors, "The principal and identity are not enrolled.");
        }

        var grants = request.Grants ?? [];
        var person = new Person(request.Id!, [identity], grants);
        var people = principal is null
            ? [.. policy.People, person]
            : policy.People.Select(existing => existing == principal ? person : existing).ToArray();
        var candidate = Serialize(policy with { People = people });
        if (Parse(candidate) is null)
        {
            return Fail(errors, "The requested grants or resulting policy failed validation.");
        }

        if (principal is not null)
        {
            if (SameGrants(principal.Grants, grants))
            {
                return Unchanged;
            }

            if (request.Operation == "enroll" && principal.Grants.Length != 0)
            {
                return Fail(errors, "The principal already has different grants. Revoke them before granting new access.");
            }
        }

        output.Write(candidate);
        return 0;
    }

    private static bool SameGrants(Grant[] existing, Grant[] requested) =>
        existing.Length == requested.Length && existing.All(grant => requested.Any(other =>
            grant.Project == other.Project && grant.Lane == other.Lane &&
            grant.Actions.Length == other.Actions.Length && grant.Actions.All(other.Actions.Contains)));

    private static int Fail(TextWriter errors, string message)
    {
        errors.WriteLine(message);
        return 2;
    }

    private sealed record Request(string? Operation, string? Id, string? Issuer, string? Subject, Grant[]? Grants);
}
