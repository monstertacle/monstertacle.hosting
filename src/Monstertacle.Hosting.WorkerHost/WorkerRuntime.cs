using Monstertacle.Multisession;
using Monstertacle.Multisession.Contracts;
using Monstertacle.Multisession.Storage.Sqlite;

#if WINDOWS
using NativeLease = Monstertacle.Multisession.Windows.WindowsStateDirectoryLease;
#else
using NativeLease = Monstertacle.Multisession.Linux.LinuxStateDirectoryLease;
#endif

namespace Monstertacle.Hosting.WorkerHost;

internal static class WorkerRuntime
{
    public static async Task<SessionRuntime> OpenAsync(string path, Guid trustScope,
        IReadOnlyList<ProviderRegistration> providers, IReadOnlyList<AgentProfile> profiles, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path) || trustScope == Guid.Empty)
        {
            throw new ArgumentException("Worker runtime requires an absolute state directory and nonempty trust scope.");
        }

#if !WINDOWS
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("WorkerHost requires Linux or native Windows.");
        }
#endif
        var exists = Directory.Exists(path);
        var lease = exists ? NativeLease.OpenExisting(path) : NativeLease.CreateNew(path);
        ISessionStore? store = null;
        SessionRuntime? runtime = null;
        try
        {
            var options = new RuntimeOptions
            {
                StateDirectory = lease.StateDirectory,
                Retention = new(true, null),
                MaxConcurrentRequests = 1,
                MaxQueuedRequests = 8
            };
            store = exists
                ? await SqliteStoreFactory.OpenExistingAsync(lease, trustScope, options, cancellationToken)
                : await SqliteStoreFactory.CreateNewAsync(lease, trustScope, options, cancellationToken);
            runtime = new SessionRuntime(store, lease, providers, profiles);
            await runtime.StartAsync(options, cancellationToken);
            return runtime;
        }
        catch
        {
            try
            {
                if (runtime is not null)
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    if (store is not null)
                    {
                        await store.DisposeAsync();
                    }
                }
                finally
                {
                    await lease.DisposeAsync();
                }
            }

            throw;
        }
    }
}
