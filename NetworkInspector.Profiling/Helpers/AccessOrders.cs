// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Helpers;

/// <summary>Id orders and the multi-threaded loop of the access scenarios.</summary>
/// <remarks><b>Thread-safety:</b> stateless; <see cref="RunOnThreads"/> only reads the shared id array.</remarks>
internal static class AccessOrders
{
    #region Constants

    private const int _Seed = 20_261_018;

    #endregion

    #region Public API

    /// <summary>Id order of one run over <paramref name="count"/> ids; the random order is a fixed-seed Fisher–Yates shuffle.</summary>
    internal static int[] Build(AccessOrder order, int count)
    {
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            ids[i] = order == AccessOrder.Reverse ? count - 1 - i : i;
        }

        if (order == AccessOrder.Random)
        {
            Random random = new(_Seed);
            random.Shuffle(ids);
        }

        return ids;
    }

    /// <summary>
    /// Calls <paramref name="read"/> for every id of <paramref name="ids"/> on each of <paramref name="threads"/> threads
    /// and returns the sum of the results. Thread <c>t</c> starts at offset <c>t · ids.Length / threads</c> and wraps, so
    /// concurrent readers walk different regions. One thread runs inline (no thread start in the measurement).
    /// </summary>
    internal static long RunOnThreads(int threads, int[] ids, Func<int, int> read)
    {
        if (threads == 1)
        {
            return _Walk(ids, 0, read);
        }

        long[] sums = new long[threads];
        Thread[] workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            int thread = t;
            int start = (int)((long)thread * ids.Length / threads);
            workers[t] = new Thread(() => sums[thread] = _Walk(ids, start, read)) { IsBackground = true };
        }

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        long total = 0;
        for (int t = 0; t < threads; t++)
        {
            workers[t].Join();
            total += sums[t];
        }

        return total;
    }

    #endregion

    #region Private helpers

    private static long _Walk(int[] ids, int start, Func<int, int> read)
    {
        long sum = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            int position = start + i;
            sum += read(ids[position < ids.Length ? position : position - ids.Length]);
        }

        return sum;
    }

    #endregion
}
