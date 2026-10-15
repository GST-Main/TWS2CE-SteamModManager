using System.Diagnostics;
using Steamworks;

namespace TWS2CESteamModManager.Steam;

internal static class SteamCall
{
    // Steam callbacks are dispatched on this same thread; no task/thread switch.
    public static T Wait<T>(Func<SteamAPICall_t> start, TimeSpan timeout,
        CancellationToken cancellationToken, Action? tick = null) where T : struct
    {
        T? completion = null;
        bool ioFailure = false;
        using var result = CallResult<T>.Create((value, failed) =>
        {
            completion = value;
            ioFailure = failed;
        });
        cancellationToken.ThrowIfCancellationRequested();
        SteamAPICall_t call = start();
        if (call == SteamAPICall_t.Invalid)
            throw new InvalidOperationException("Steam could not start the request.");
        result.Set(call);
        PumpUntil(() => completion.HasValue, timeout, cancellationToken, tick);
        if (ioFailure)
            throw new InvalidOperationException("Steam reported an I/O failure. Check your connection and item access.");
        return completion!.Value;
    }

    public static void PumpUntil(Func<bool> completed, TimeSpan timeout,
        CancellationToken cancellationToken, Action? tick = null)
    {
        var timer = Stopwatch.StartNew();
        var reportTimer = Stopwatch.StartNew();
        while (!completed())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= timeout)
                throw new TimeoutException($"Steam request timed out after {timeout.TotalSeconds:0} seconds.");
            SteamAPI.RunCallbacks();
            if (reportTimer.Elapsed >= TimeSpan.FromSeconds(1))
            {
                tick?.Invoke();
                reportTimer.Restart();
            }
            if (!completed())
                Thread.Sleep(10);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
