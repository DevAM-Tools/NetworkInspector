// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// A frame source whose reads fail on a schedule: reads <c>failAtRead</c> to <c>failAtRead + failCount - 1</c> (counted from 1)
/// throw an <see cref="IOException"/>, every other read is passed on to the inner source (a source without an inner source
/// ends with <see langword="null"/>). After <see cref="SafetyStop"/> reads it throws <see cref="OperationCanceledException"/>, so
/// a loop that never ends fails its test quickly instead of hanging it. Not thread-safe; one loop drives it.
/// </summary>
/// <param name="inner">The source that supplies the frames of the successful reads, or <see langword="null"/>.</param>
/// <param name="failAtRead">Number of the first failing read, counted from 1.</param>
/// <param name="failCount">How many reads in a row fail.</param>
internal sealed class FailingFrameSource(IFrameSource? inner, int failAtRead, int failCount) : IFrameSource
{
    /// <summary>Number of reads after which the source throws a cancellation, so a runaway loop ends.</summary>
    internal const int SafetyStop = 10_000;

    /// <summary>Gets the number of reads so far.</summary>
    internal int Reads { get; private set; }

    /// <inheritdoc/>
    public string UiName => "failing-source";

    /// <inheritdoc/>
    public string? Description => null;

    /// <inheritdoc/>
    public int? EstimatedFrameCount => null;

    /// <inheritdoc/>
    public bool IsRunning => true;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry) => inner?.Start(sourceId, registry);

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        Reads++;
        if (Reads > SafetyStop)
        {
            throw new OperationCanceledException("The loop did not stop reading a failing source.");
        }

        if (Reads >= failAtRead && Reads - failAtRead < failCount)
        {
            throw new IOException("read failed");
        }

        return inner?.NextFrame(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose() => inner?.Dispose();
}
