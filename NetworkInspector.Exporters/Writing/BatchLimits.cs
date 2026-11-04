// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Writing;

/// <summary>Shared validation of the batch-size limits of the batching exporters (one rule, one message for all formats).</summary>
internal static class BatchLimits
{
    // Limit: packets per batch, 1 to int.MaxValue, default 50 000; estimated bytes per batch, 1 to long.MaxValue, default
    // 16 MiB. Threat: memory exhaustion, because a batch is held in memory until it is written and two batches exist in
    // background mode. Configurable by the library caller (builders) and the CLI user (format options batchPackets,
    // batchBytes). Posture: secure by default — the defaults keep an export small on any host; raising them trades memory for
    // fewer, larger writes (transactions, row groups, blocks). Rejected: a default derived from host memory, because the size
    // is an estimate too coarse for a host-sized bound. Residual risk: a caller who raises both to the maximum can exhaust
    // memory on purpose; the builder documentation warns about it.

    /// <summary>Default packets per batch (secure by default: small enough for any host).</summary>
    internal const int DefaultPacketsPerBatch = 50_000;

    /// <summary>Default estimated bytes per batch (16 MiB).</summary>
    internal const long DefaultBytesPerBatch = 16L * 1024 * 1024;

    /// <summary>Checks the packets-per-batch limit and returns it.</summary>
    /// <param name="count">Requested packets per batch.</param>
    /// <param name="parameterName">Name of the caller's parameter, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1; the message names the range.</exception>
    internal static int ValidatePacketsPerBatch(int count, string parameterName)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, count, Invariant($"Packets per batch must be between 1 and {int.MaxValue}."));
        }

        return count;
    }

    /// <summary>Checks the estimated-bytes-per-batch limit and returns it.</summary>
    /// <param name="bytes">Requested estimated bytes per batch.</param>
    /// <param name="parameterName">Name of the caller's parameter, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is below 1; the message names the range.</exception>
    internal static long ValidateBytesPerBatch(long bytes, string parameterName)
    {
        if (bytes < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, bytes, Invariant($"Estimated bytes per batch must be between 1 and {long.MaxValue}."));
        }

        return bytes;
    }
}
