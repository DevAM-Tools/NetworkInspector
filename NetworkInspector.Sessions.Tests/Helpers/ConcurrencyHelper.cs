// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>Starts several threads at the same instant so a race test begins all contenders together.</summary>
internal static class ConcurrencyHelper
{
    /// <summary>How long a contender waits for the others before the test gives up instead of hanging.</summary>
    private static readonly TimeSpan _StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="body"/> on <paramref name="threadCount"/> dedicated threads, released together by a barrier,
    /// and waits for all of them. The first exception thrown by a body is rethrown on the calling thread.
    /// Dedicated threads (not the thread pool) so a barrier cannot starve while the pool is still growing.
    /// </summary>
    internal static void RunTogether(int threadCount, Action<int> body)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);
        ArgumentNullException.ThrowIfNull(body);

        using Barrier barrier = new(threadCount);
        Exception? failure = null;
        Thread[] threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int index = i;
            threads[i] = new Thread(() =>
            {
                // Top-level boundary of a test thread: the exception is handed to the calling thread, not swallowed.
                try
                {
                    if (!barrier.SignalAndWait(_StartTimeout))
                    {
                        throw new TimeoutException("The contenders did not all arrive at the barrier.");
                    }

                    body(index);
                }
                catch (Exception ex)
                {
                    _ = Interlocked.CompareExchange(ref failure, ex, null);
                }
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
