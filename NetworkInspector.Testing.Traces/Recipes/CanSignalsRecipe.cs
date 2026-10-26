// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// A PCAPNG trace (link type SocketCAN) of the vehicle dynamics message <see cref="CanSignalTrace.CanId"/>: eight data bytes
/// encoded with <see cref="SignalConfigFixture.Layout"/>. Vehicle speed is raw 254 in every 50th frame and raw 255 in every
/// 51st frame (a frame at both positions, every index divisible by 2,550 including 0, takes 254), else <c>i % 254</c> (so the
/// ordinary raw values 0 to 253 never collide with the two special values); gear is <c>i % 5</c>; the wheel speed raw
/// value is <c>i * 7 % 30000</c> (physical raw × 0.01). The side file <c>signals.json</c> holds the signal configuration and
/// the returned profile setting points the decoder at it. Immutable; thread-safe.
/// </summary>
public sealed class CanSignalsRecipe : TraceRecipe
{
    #region Constants

    /// <summary>Frames between two cancellation checks: often enough to stop a large write quickly, rare enough to cost nothing.</summary>
    private const int _CancellationCheckInterval = 1024;

    /// <summary>Distance of two frame timestamps: 1 ms in nanoseconds.</summary>
    private const long _FrameIntervalNanos = 1_000_000;

    #endregion

    #region Lifecycle

    /// <summary>Creates the recipe.</summary>
    /// <param name="frameCount">Number of frames, 1 to <see cref="TraceRecipe.MaxFrames"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCount"/> is out of range.</exception>
    public CanSignalsRecipe(int frameCount)
    {
        CheckCount(frameCount, nameof(frameCount));
        Frames = frameCount;
    }

    #endregion

    #region Properties

    /// <summary>Number of frames.</summary>
    public int Frames { get; }

    /// <inheritdoc/>
    public override string Name => "can-signals";

    /// <inheritdoc/>
    public override int Version => 1;

    /// <inheritdoc/>
    public override string Extension => ".pcapng";

    /// <inheritdoc/>
    public override int FrameCount => Frames;

    /// <inheritdoc/>
    public override string CanonicalParameters => string.Create(CultureInfo.InvariantCulture, $"count={Frames}");

    #endregion

    #region Public API

    /// <summary>The raw vehicle speed of frame <paramref name="index"/> (254, 255, or <c>index % 254</c>).</summary>
    /// <param name="index">Zero-based frame index.</param>
    /// <returns>The raw value.</returns>
    public static ulong RawSpeedFor(int index)
    {
        // The order decides the frames that are multiples of both 50 and 51: the error value 254 wins there. Swapping the
        // checks would change the written bytes and therefore needs a new recipe version.
        if (index % 50 == 0)
        {
            return 254;
        }

        return index % 51 == 0
            ? 255UL
            : (ulong)(index % 254);
    }

    /// <summary>The raw wheel speed of frame <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based frame index.</param>
    /// <returns>The raw 16-bit value.</returns>
    public static ulong RawWheelSpeedFor(int index) =>
        // The product is widened to 64 bits so that no 32-bit overflow has to be ruled out; the result is below 30,000 and
        // therefore fits the 16-bit signal.
        (ulong)(((long)index * 7) % 30000);

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected internal override IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken)
    {
        // The configuration is a side file at its final path (the folder exists and is empty); the returned setting holds
        // that absolute path, which is why the cache folder must be usable as the settings storage of a test (see TraceFile).
        string configPath = Path.Combine(sideFileDirectory, "signals.json");
        SignalConfigFixture.WriteConfig(configPath);

        // One data buffer is reused for all frames: the emit step copies it into the new frame before the next iteration
        // rewrites it, so the loop allocates only the frame arrays. Test support is cached once, so this is not a hot path.
        using PcapngTraceWriter writer = new(tracePath, LinkType.CanSocketcan);
        byte[] data = new byte[SignalConfigFixture.ByteLength];
        for (int i = 0; i < Frames; i++)
        {
            if (i % _CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The layout encodes the three raw values into the eight data bytes; the CAN layer wraps them in a SocketCAN frame.
            // The buffer is not cleared here: WriteHeader of a layout-based message zeroes the destination itself before it
            // writes the signals, so bits that no signal covers are zero in every frame, whatever the previous frame left.
            SignalMessageValueSet values = SignalMessageValueSet.For(SignalConfigFixture.Layout)
                .SetRaw(SignalConfigFixture.SpeedName, RawSpeedFor(i))
                .SetRaw(SignalConfigFixture.GearName, (ulong)(i % 5))
                .SetRaw(SignalConfigFixture.WheelSpeedName, RawWheelSpeedFor(i));
            new SignalMessageLayer(SignalConfigFixture.Layout, values).WriteHeader(data);

            SocketCanLayer can = new(CanSignalTrace.CanId, data);
            writer.Add(i * _FrameIntervalNanos, FrameStack.Start(can).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty));
        }

        writer.Finish();
        return new Dictionary<string, string> { [CanSignalTrace.ConfigFileSetting] = configPath };
    }

    #endregion
}
