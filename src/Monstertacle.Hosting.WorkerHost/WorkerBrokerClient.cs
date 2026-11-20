using System.Net;
using System.Net.Http.Json;
using Monstertacle.Hosting.Contracts;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class WorkerBrokerClient(HttpClient client)
{
    public async Task<JobClaim?> ClaimAsync(Guid mutation, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(WorkerContract.Route + "/claims", new ClaimJob(mutation), token);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await Read<JobClaim>(response, token);
    }

    public async Task<JobClaim> StatusAsync(JobClaim claim, CancellationToken token)
    {
        using var response = await client.GetAsync(PathFor(claim), token);
        return await Read<JobClaim>(response, token);
    }

    public async Task<JobDispatch?> AuthorizeAsync(JobClaim claim, CancellationToken token)
    {
        using var response = await client.PostAsync(PathFor(claim) + "/dispatch", null, token);
        return response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict
            ? null : await Read<JobDispatch>(response, token);
    }

    public async Task ReportAsync(JobClaim claim, JobObservation observation, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(PathFor(claim) + "/observation", observation, token);
        response.EnsureSuccessStatusCode();
    }

    public async Task CompleteAsync(JobClaim claim, JobCompletion completion, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync(PathFor(claim) + "/result", completion, token);
        response.EnsureSuccessStatusCode();
    }

    private static string PathFor(JobClaim claim) =>
        $"{WorkerContract.Route}/jobs/{claim.JobId:D}/claims/{claim.Fence:D}";

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("The broker returned no worker response.");
    }
}
