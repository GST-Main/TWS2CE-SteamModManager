using System.Collections.Concurrent;
using Steamworks;
using TWS2CESteamModManager.Core;

namespace TWS2CESteamModManager.Steam;

public interface IWorkshopBrowserService : IDisposable
{
    Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default);
    Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default);
    Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default);
}

public interface IWorkshopModInstaller
{
    Task<IReadOnlyList<ManagedMod>> InstallAsync(ModManager manager, ulong itemId,
        Action<string>? report = null, CancellationToken cancellationToken = default);
}

public interface IWorkshopModRestorer
{
    Task RestoreAsync(ModManager manager, Guid modId, Action<string>? report = null,
        CancellationToken cancellationToken = default);
}

public class SteamUnavailableException(string message) : InvalidOperationException(message);
public sealed class SteamNotRunningException() : SteamUnavailableException("Steam must be running to browse Workshop.");

/// <summary>Owns Steam initialization, callback pumping and queries on one dedicated worker.</summary>
public sealed class SteamWorkshopBrowserService : IWorkshopBrowserService, IWorkshopModInstaller, IWorkshopModRestorer
{
    private readonly BlockingCollection<IJob> jobs = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Thread thread;

    private interface IJob
    {
        void Execute();
        void Fail(Exception exception);
        void Cancel();
    }

    private sealed class Job<T>(Func<CancellationToken, T> work, CancellationTokenSource cancellation) : IJob
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Execute()
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Completion.TrySetResult(work(cancellation.Token));
            }
            catch (OperationCanceledException) { Completion.TrySetCanceled(cancellation.Token); }
            catch (Exception exception) { Completion.TrySetException(exception); }
            finally { cancellation.Dispose(); }
        }
        public void Fail(Exception exception) { Completion.TrySetException(exception); cancellation.Dispose(); }
        public void Cancel() { Completion.TrySetCanceled(); cancellation.Dispose(); }
    }

    public SteamWorkshopBrowserService()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Steam Workshop worker" };
        thread.Start();
    }

    public Task<IReadOnlyList<WorkshopTag>> FetchTagsAsync(CancellationToken cancellationToken = default) =>
        WorkshopTags.FetchAsync(cancellationToken);

    public Task<WorkshopPage> FetchItemsAsync(WorkshopOptions options, CancellationToken cancellationToken = default) =>
        Enqueue(token => WorkshopCatalog.Fetch(options, token), cancellationToken);

    public Task<WorkshopItemDetails> FetchDetailsAsync(ulong itemId, CancellationToken cancellationToken = default) =>
        Enqueue(token => WorkshopDetails.Fetch(itemId, token), cancellationToken);

    public Task<IReadOnlyList<ManagedMod>> InstallAsync(ModManager manager, ulong itemId,
        Action<string>? report = null, CancellationToken cancellationToken = default) =>
        Enqueue(token => manager.Download(itemId, report, token), cancellationToken);

    public Task RestoreAsync(ModManager manager, Guid modId, Action<string>? report = null,
        CancellationToken cancellationToken = default) => Enqueue(token =>
        {
            manager.SetEnabled(modId, true, report, token);
            return true;
        }, cancellationToken);

    private Task<T> Enqueue<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken)
    {
        if (shutdown.IsCancellationRequested) return Task.FromCanceled<T>(new CancellationToken(true));
        var job = new Job<T>(work, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token));
        try { jobs.Add(job); }
        catch (InvalidOperationException) { job.Cancel(); }
        return job.Completion.Task;
    }

    private void Run()
    {
        bool initialized = false;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                if (jobs.TryTake(out IJob? job, 25))
                {
                    if (!initialized)
                    {
                        try
                        {
                            if (!SteamAPI.IsSteamRunning()) throw new SteamNotRunningException();
                            initialized = SteamAPI.Init();
                            if (!initialized)
                                throw new SteamUnavailableException("Steam is unavailable. Start Steam, sign in to an account with SHOGUN 2, then retry.");
                            if (SteamUtils.GetAppID().m_AppId != GameConstants.WorkshopAppId)
                            {
                                SteamAPI.Shutdown();
                                initialized = false;
                                throw new SteamUnavailableException("Steam opened the wrong app context. Check steam_appid.txt contains 34330.");
                            }
                        }
                        catch (Exception exception) { job.Fail(exception); continue; }
                    }
                    job.Execute();
                }
                if (initialized) SteamAPI.RunCallbacks();
            }
        }
        finally
        {
            while (jobs.TryTake(out IJob? pending)) pending.Cancel();
            if (initialized) SteamAPI.Shutdown();
        }
    }

    public void Dispose()
    {
        if (shutdown.IsCancellationRequested) return;
        shutdown.Cancel();
        jobs.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
        // Tokens/queue stay valid until a slow native initialization returns.
    }
}
