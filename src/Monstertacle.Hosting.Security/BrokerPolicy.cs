using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monstertacle.Hosting.Security;

internal sealed class BrokerPolicy(IConfiguration configuration, string? workloadIssuer)
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string? path = configuration["PolicyFile"];

    public Access? Resolve(ClaimsPrincipal user) =>
        Resolve(new Identity(user.FindFirstValue("iss"), user.FindFirstValue("sub")));

    public Access? Resolve(Identity identity)
    {
        if (Read() is not { } policy)
        {
            return null;
        }

        if (workloadIssuer is not null && identity.Issuer == workloadIssuer)
        {
            return ActiveWorkload(policy) is { } workload && workload.Identities.Contains(identity)
                ? new Access(workload, identity, policy.Projects)
                : null;
        }

        var person = policy.People.SingleOrDefault(person => person.Identities.Contains(identity));
        return person is null ? null : new Access(person, identity, policy.Projects);
    }

    public IEnumerable<Person> Claimants(Policy policy) =>
        ActiveWorkload(policy) is { } workload ? policy.People.Append(workload) : policy.People;

    private Person? ActiveWorkload(Policy policy)
    {
        if (policy.Workload is not { } workload || workload.Identity.Issuer != workloadIssuer ||
            workload.ExpiresUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var binding = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(workload.Identity, Json)));
        return new Person($"workload:{workload.Id}:{binding}", [workload.Identity], workload.Grants);
    }

    public Policy? Read()
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumBytes)
            {
                return null;
            }

            var length = (int)input.Length;
            var contents = new byte[length];
            input.ReadExactly(contents);
            if (input.ReadByte() != -1)
            {
                return null;
            }

            var policy = JsonSerializer.Deserialize<Policy>(contents, Json);
            return Valid(policy) ? policy : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Valid(Policy? policy)
    {
        if (policy is not { Version: 1, People: not null, Projects: { Length: > 0 } } ||
            policy.Projects.Any(project => project is null || !ValidName(project.Id) ||
                project.Lanes is not { Length: > 0 and <= 8 } ||
                project.Lanes.Any(lane => !ValidName(lane)) ||
                !Unique(project.Lanes)) ||
            !Unique(policy.Projects.Select(project => project.Id)))
        {
            return false;
        }

        var projects = policy.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        if (policy.Workload is { } workload &&
            (!ValidName(workload.Id) || !ValidIdentity(workload.Identity) || workload.ExpiresUtc == default ||
             workload.Project is null || !projects.ContainsKey(workload.Project) ||
             workload.Grants is null || !workload.Grants.All(grant =>
                 ValidGrant(grant, projects) && grant.Project == workload.Project &&
                 grant.Actions.All(action => action is "start" or "read")) ||
             !Unique(workload.Grants.Select(grant => grant.Lane))))
        {
            return false;
        }

        return policy.People.All(person => person is not null && ValidName(person.Id) &&
                   person.Identities is { Length: > 0 } && person.Identities.All(ValidIdentity) &&
                   person.Grants is not null && person.Grants.All(grant => ValidGrant(grant, projects)) &&
                   Unique(person.Grants.Select(grant => (grant.Project, grant.Lane)))) &&
               Unique(policy.People.Select(person => person.Id)) &&
               Unique(policy.People.SelectMany(person => person.Identities));
    }

    private static bool ValidIdentity(Identity? identity) =>
        identity is not null && Uri.TryCreate(identity.Issuer, UriKind.Absolute, out var issuer) &&
        issuer.Scheme == Uri.UriSchemeHttps && !string.IsNullOrWhiteSpace(identity.Subject);

    private static bool ValidGrant(Grant? grant, Dictionary<string, Project> projects) =>
        grant is not null && grant.Project is not null && projects.TryGetValue(grant.Project, out var project) &&
        grant.Lane is not null && project.Lanes.Contains(grant.Lane, StringComparer.Ordinal) &&
        grant.Actions is { Length: > 0 } && grant.Actions.All(IsKnownAction) && Unique(grant.Actions);

    internal static bool ValidName(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool Unique<T>(IEnumerable<T> values)
    {
        var seen = new HashSet<T>();
        return values.All(seen.Add);
    }

    private static bool IsKnownAction(string? action) => action is "start" or "read" or "cancel" or "result" or "artifact" or "accept-result";

    internal sealed record Policy(int Version, Person[] People, Project[] Projects, Workload? Workload);
    internal sealed record Workload(string Id, Identity Identity, DateTimeOffset ExpiresUtc, string Project, Grant[] Grants);
    internal sealed record Identity(string? Issuer, string? Subject);
    internal sealed record Person(string Id, Identity[] Identities, Grant[] Grants);
    internal sealed record Grant(string Project, string Lane, string[] Actions);
    internal sealed record Project(string Id, string[] Lanes);

    internal sealed record Access(Person Person, Identity Identity, IReadOnlyList<Project> Projects)
    {
        public string Principal => Person.Id;

        public string[]? Plan(string project) =>
            Projects.SingleOrDefault(item => string.Equals(item.Id, project, StringComparison.Ordinal))?.Lanes;

        public bool Allows(string project, IReadOnlyList<string> lanes, string action) =>
            Plan(project) is { } registered && lanes.Count > 0 &&
            lanes.All(lane => registered.Contains(lane, StringComparer.Ordinal) &&
                              Person.Grants.Any(grant => grant.Project == project && grant.Lane == lane &&
                                  grant.Actions.Contains(action, StringComparer.Ordinal)));

        public bool HasGrant(string project, string action) =>
            Plan(project) is not null && Person.Grants.Any(grant =>
                grant.Project == project && grant.Actions.Contains(action, StringComparer.Ordinal));

        public IEnumerable<string> GrantedLanes(string project, string action) =>
            Person.Grants.Where(grant => grant.Project == project &&
                grant.Actions.Contains(action, StringComparer.Ordinal)).Select(grant => grant.Lane);
    }
}
