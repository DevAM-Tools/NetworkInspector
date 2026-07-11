// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>Shared spin-wait helpers for session integration tests.</summary>
internal static class WaitHelper
{
    /// <summary>
    /// Spins for up to <paramref name="timeoutMs"/> until <paramref name="condition"/>
    /// returns <see langword="true"/>.
    /// </summary>
    internal static void WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        Stopwatch sw = Stopwatch.StartNew();
        SpinWait wait = new();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Condition was not met within {timeoutMs.ToString(CultureInfo.InvariantCulture)} ms.");
            }

            wait.SpinOnce();
        }
    }
}
