// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Proves that a single-writer gate rejects a second writer, without depending on the scheduler running two
/// fixed-size loops at the same time.
/// </summary>
/// <remarks>
/// Each round creates a fresh store, releases both writers from one <see cref="Barrier"/>, and lets the second writer
/// keep calling until the first writer finished its burst, so the rejection is observed as soon as both are inside the
/// gate window. Rounds repeat (bounded) when the scheduler ran the writers back to back. A store without the gate never
/// rejects, so the probe returns <see langword="null"/> after the last round and the calling test fails.
/// </remarks>
internal static class ConcurrentWriterProbe
{
    /// <summary>Rounds before the probe gives up; one round normally suffices.</summary>
    internal const int DefaultRounds = 64;

    /// <summary>
    /// Runs up to <see cref="DefaultRounds"/> rounds on fresh stores and returns the store of the first round in which
    /// a writer got <see cref="InvalidOperationException"/>; <see langword="null"/> when no round observed a rejection.
    /// </summary>
    /// <typeparam name="TStore">Store under test.</typeparam>
    /// <param name="createStore">Creates the store of one round.</param>
    /// <param name="first">First writer; receives its call counter so it can build distinct keys.</param>
    /// <param name="second">Second writer; receives its own call counter.</param>
    /// <param name="burst">Number of calls of the first writer per round.</param>
    internal static async Task<TStore?> SecondWriterIsRejectedAsync<TStore>(
        Func<TStore> createStore,
        Action<TStore, int> first,
        Action<TStore, int> second,
        int burst)
        where TStore : class
    {
        for (int round = 0; round < DefaultRounds; round++)
        {
            TStore store = createStore();
            if (await _RunRoundAsync(store, first, second, burst))
            {
                return store;
            }
        }

        return null;
    }

    /// <summary>One round: both writers start together; the second runs until the first is done or rejected.</summary>
    private static async Task<bool> _RunRoundAsync<TStore>(TStore store, Action<TStore, int> first, Action<TStore, int> second, int burst)
    {
        using Barrier start = new(2);
        int firstDone = 0;
        int rejected = 0;

        // First writer: a fixed burst; a rejection ends it early. The finally ends the second writer's loop even
        // when the first writer fails with another exception.
        Task firstTask = _StartDedicated(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int call = 0; call < burst; call++)
                {
                    try
                    {
                        first(store, call);
                    }
                    catch (InvalidOperationException)
                    {
                        _ = Interlocked.Exchange(ref rejected, 1);
                        break;
                    }
                }
            }
            finally
            {
                _ = Interlocked.Exchange(ref firstDone, 1);
            }
        });

        // Second writer: keeps calling while the first is inside its burst, so the two overlap whenever both run.
        Task secondTask = _StartDedicated(() =>
        {
            start.SignalAndWait();
            int call = 0;
            while (Volatile.Read(ref firstDone) == 0)
            {
                try
                {
                    second(store, call);
                }
                catch (InvalidOperationException)
                {
                    _ = Interlocked.Exchange(ref rejected, 1);
                    return;
                }

                call = unchecked(call + 1);
            }
        });

        await Task.WhenAll(firstTask, secondTask);
        return Volatile.Read(ref rejected) == 1;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated thread: both writers block in <see cref="Barrier.SignalAndWait()"/>, which on
    /// pool threads could stall a busy test run until the pool injects a thread.
    /// </summary>
    private static Task _StartDedicated(Action body) =>
        Task.Factory.StartNew(body, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
