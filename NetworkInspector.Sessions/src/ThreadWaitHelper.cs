// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Spin-then-sleep wait helpers for non-hot-path blocking (job join, shutdown drain).
/// <see cref="Session"/> uses a tight <see cref="SpinWait"/> loop for concurrent
/// <see cref="Session.Shutdown"/> callers where minimal latency is preferred.
/// Caps busy-spin before yielding to avoid burning a core when work is slow or stuck.
/// </summary>
internal static class ThreadWaitHelper
{
    private const int _SpinIterationsBeforeSleep = 1024;

    /// <summary>Blocks until <paramref name="condition"/> returns <see langword="true"/>.</summary>
    internal static void WaitUntil(Func<bool> condition)
    {
        SpinWait spinner = new();
        int spins = 0;
        while (!condition())
        {
            if (spins++ >= _SpinIterationsBeforeSleep)
            {
                Thread.Sleep(1);
                spins = 0;
            }
            else
            {
                spinner.SpinOnce();
            }
        }
    }

    /// <summary>
    /// Blocks until <paramref name="condition"/> returns <see langword="true"/> or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns><see langword="true"/> when the condition was met before timeout.</returns>
    internal static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SpinWait spinner = new();
        int spins = 0;
        while (!condition())
        {
            if (sw.Elapsed >= timeout)
            {
                return false;
            }

            if (spins++ >= _SpinIterationsBeforeSleep)
            {
                Thread.Sleep(1);
                spins = 0;
            }
            else
            {
                spinner.SpinOnce();
            }
        }

        return true;
    }
}
