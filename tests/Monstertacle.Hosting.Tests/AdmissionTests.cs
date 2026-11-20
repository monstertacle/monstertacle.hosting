using Monstertacle.Hosting.Contracts;

namespace Monstertacle.Hosting.Tests;

public sealed class AdmissionTests
{
    private readonly Guid mutation = Guid.NewGuid();

    [Fact]
    public void AdmissionPayloadDigestSeparatesFieldsAndExcludesMutationIdentity()
    {
        var request = new StartSession(mutation, "ab", "c");
        var samePayload = request with { MutationId = Guid.NewGuid() };

        Assert.Equal(request.GetPayloadDigest(), samePayload.GetPayloadDigest());
        Assert.NotEqual(request.GetPayloadDigest(), new StartSession(mutation, "a", "bc").GetPayloadDigest());
        Assert.NotEqual(request.GetPayloadDigest(), (request with { Task = "c " }).GetPayloadDigest());
    }

    [Theory]
    [InlineData("invalid/path", "task")]
    [InlineData("", "task")]
    [InlineData("project", " ")]
    public void InvalidRequestsCannotProduceAdmissionDigests(string project, string task)
    {
        var request = new StartSession(mutation, project, task);
        Assert.False(request.IsValid());
        Assert.Throws<InvalidOperationException>(request.GetPayloadDigest);
    }

    [Fact]
    public void SizeLimitUsesUtf8BytesAndMutationCannotBeEmpty()
    {
        var request = new StartSession(mutation, "project", new string('é', SessionContract.MaxTaskBytes / 2));

        Assert.True(request.IsValid());
        Assert.False((request with { Task = request.Task + "a" }).IsValid());
        Assert.False((request with { MutationId = Guid.Empty }).IsValid());
    }
}
