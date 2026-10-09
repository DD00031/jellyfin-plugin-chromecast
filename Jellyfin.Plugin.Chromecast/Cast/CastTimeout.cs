using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Chromecast.Cast;

/// <summary>
/// SharpCaster matches responses to requests without any timeout, and a write into a connection
/// whose peer was unplugged blocks once the socket buffer is full - so anything awaited from it
/// can hang forever, silently. Every call to the device goes through here instead.
/// </summary>
internal static class CastTimeout
{
    /// <summary>The limit for a single command to a device that is expected to be connected.</summary>
    public static readonly TimeSpan Command = TimeSpan.FromSeconds(5);

    /// <summary>The limit for opening a connection (TCP, TLS and the first status exchange).</summary>
    public static readonly TimeSpan Connect = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Awaits <paramref name="task"/>, giving up after <paramref name="timeout"/>.
    /// </summary>
    /// <param name="task">The call to the device.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="what">What was being done, for the error message.</param>
    /// <exception cref="TimeoutException">The device did not answer in time.</exception>
    /// <returns>A task that completes with the call, or faults with a timeout.</returns>
    public static async Task WithTimeoutAsync(this Task task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
        {
            // Keep a late failure of the abandoned call from surfacing as an unobserved exception.
            _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw new TimeoutException($"{what} got no answer within {timeout.TotalSeconds:0} seconds");
        }

        await task.ConfigureAwait(false);
    }

    /// <inheritdoc cref="WithTimeoutAsync(Task, TimeSpan, string)"/>
    /// <typeparam name="T">The result type.</typeparam>
    public static async Task<T> WithTimeoutAsync<T>(this Task<T> task, TimeSpan timeout, string what)
    {
        await ((Task)task).WithTimeoutAsync(timeout, what).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }
}
