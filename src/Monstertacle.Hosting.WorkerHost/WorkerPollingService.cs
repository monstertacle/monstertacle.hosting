using System.Net;

namespace Monstertacle.Hosting.WorkerHost;

internal sealed class WorkerPollingService(WorkerDispatch dispatch, ILogger<WorkerPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await dispatch.PollAsync(stoppingToken);
            }
            catch (HttpRequestException error) when (error.StatusCode is null or HttpStatusCode.ServiceUnavailable
                       or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Worker broker connection is temporarily unavailable; the current assignment is retained.");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Worker operation timed out; the current assignment is retained.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
