// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace copied from a <see cref="RandomFrameSource"/> with a fixed seed, mode, base time (2026-01-01T00:00:00Z), and
/// interval (1 µs), so the same parameters always give the same bytes. The frame count may be as large as 200 million for the
/// performance suites; the functional tests use a few thousand. Immutable; thread-safe.
/// </summary>
public sealed class LargeRandomRecipe : TraceRecipe
{
    #region Constants

    // Limit: frames, 1 to 200,000,000. Test support only, so no threat from input: it exists for the performance suites
    // that need very large captures and is still a bound against a typo. Constant, not configurable; the general recipe
    // bound (TraceRecipe.MaxFrames) is raised for this recipe only because it streams (below) and needs no memory per frame.
    /// <summary>Largest frame count of this recipe.</summary>
    public const int MaxLargeFrames = 200_000_000;

    /// <summary>Distance of two frame timestamps: 1 microsecond in nanoseconds.</summary>
    private const long _IntervalNanos = 1_000;

    /// <summary>Frames between two cancellation checks.</summary>
    private const int _CancellationCheckInterval = 4096;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="MaxLargeFrames"/>.</param>
    /// <param name="seed">Seed of the random source.</param>
    /// <param name="mode">Frame mode of the random source.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is out of range or <paramref name="mode"/> is undefined.</exception>
    public LargeRandomRecipe(int frameCount, ulong seed, RandomFrameMode mode)
    {
        CheckCount(frameCount, nameof(frameCount), MaxLargeFrames);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The frame mode is not defined.");
        }

        Frames = frameCount;
        Seed = seed;
        Mode = mode;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <summary>Seed of the random source.</summary>
    public ulong Seed { get; }

    /// <summary>Frame mode of the random source.</summary>
    public RandomFrameMode Mode { get; }

    /// <inheritdoc/>
    public override string Name => "large-random";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters =>
        string.Create(CultureInfo.InvariantCulture, $"count={Frames};seed={Seed};mode={Mode}");

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        // Determinism: the explicit seed, base time, and interval fix every byte and timestamp (the Core source guarantees the
        // same bytes on every CPU for an explicit seed). The source and the exporter are chained frame by frame instead of
        // collecting frames, because a trace of up to 200 million frames cannot be held in memory.
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = Frames,
            Seed = Seed,
            Mode = Mode,
            BaseTimestamp = Timestamp.FromNanos(BaseNanos),
            TimestampInterval = _IntervalNanos,
        });
        source.Start(registry.RegisterSource(source), registry);

        using PcapngExporter exporter = PcapngExporter.CreateBuilder().ToFile(tracePath).Build();
        int written = 0;
        while (written < Frames)
        {
            if (written % _CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Frame? frame = source.NextFrame(cancellationToken);
            if (frame is null)
            {
                break;
            }

            exporter.OnFrame(frame.Value);
            written++;
        }

        // Some modes (TCP streams) end before the requested count; a trace that does not hold FrameCount frames would make every
        // caller's expectation wrong, so it is an error here and not a short file.
        if (written != Frames)
        {
            throw new InvalidOperationException(Invariant($"The random source with mode {Mode} delivered {written} of {Frames} frames."));
        }

        exporter.OnFinish();
        return FrozenDictionary<string, string>.Empty;
    }

    #endregion
}
