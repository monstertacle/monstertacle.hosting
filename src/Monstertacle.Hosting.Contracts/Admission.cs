using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Monstertacle.Hosting.Contracts;

public static class SessionContract
{
    public const int Version = 1;
    public const string Route = "/v1/sessions";
    public const int MaxTaskBytes = 16 * 1024;
    public const int MaxProjectLength = 64;
}

public sealed record StartSession(Guid MutationId, string Project, string Task)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public bool IsValid()
    {
        if (MutationId == Guid.Empty ||
            Project is null or { Length: 0 or > SessionContract.MaxProjectLength } ||
            !Project.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
            string.IsNullOrWhiteSpace(Task))
        {
            return false;
        }

        try
        {
            return Utf8.GetByteCount(Task) <= SessionContract.MaxTaskBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public string GetPayloadDigest()
    {
        if (!IsValid())
        {
            throw new InvalidOperationException("The admission request is invalid.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "monstertacle.hosting.start.v1");
        Append(hash, Project);
        Append(hash, Task);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Utf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed record SessionStatus(
    Guid SessionId,
    string Project,
    DateTimeOffset AdmittedUtc,
    DateTimeOffset ObservedUtc,
    IReadOnlyList<JobStatus> Jobs);

public sealed record JobStatus(Guid JobId, string Lane, string State, bool CancellationRequested = false);

public sealed record SessionPage(IReadOnlyList<SessionStatus> Sessions, Guid? NextAfter);
