// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Generates sample frames: frame <c>i</c> carries counter <c>startCounter + i</c>, gear <c>i % 5</c>, and speed
/// <see cref="SampleFrameWriter.SpeedFor"/>, timestamp 2026-01-01T00:00:00Z + <c>i</c> ms. Every frame is regenerated from
/// its index, so random access needs no shared buffer.
/// <para>
/// Thread safety: <see cref="NextFrame"/> belongs to one sequential reader; <see cref="FrameById"/> and the statistics are
/// safe from any thread after <see cref="Start"/>, because they only read values published by the volatile start flag.
/// </para>
/// </summary>
internal sealed class SampleFrameSource : IRandomAccessFrameSource, IFrameSourceStatistics
{
    #region Constants

    // 2026-01-01T00:00:00Z in seconds since the epoch.
    private const long _BaseSeconds = 1_767_225_600;

    private const long _NanosPerMilli = 1_000_000;

    // Gear cycles through the five values P, R, N, D, S.
    private const int _GearCount = 5;

    #endregion

    #region Fields

    private readonly int _Count;

    private readonly bool _Tcp;

    private readonly ushort _Port;

    private readonly ulong _StartCounter;

    // Read and written only by the sequential reader (NextFrame).
    private int _FrameIndex;

    private int _ReadCount;

    // Written in Start, then published through the volatile flag; FrameById reads them after reading the flag.
    private FrameInterfaceRegistry? _Registry;

    private FrameInterfaceId _InterfaceId;

    private volatile bool _Started;

    private int _Disposed;

    #endregion

    #region Lifecycle

    /// <summary>Creates a not yet started source.</summary>
    /// <param name="count">Number of frames; 0 yields none.</param>
    /// <param name="tcp"><see langword="true"/> for TCP frames, <see langword="false"/> for UDP frames.</param>
    /// <param name="port">Destination port of the UDP or TCP frames.</param>
    /// <param name="startCounter">Counter of the first frame.</param>
    internal SampleFrameSource(int count, bool tcp, ushort port, ulong startCounter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfZero(port);
        _Count = count;
        _Tcp = tcp;
        _Port = port;
        _StartCounter = startCounter;
    }

    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(ref _Disposed, 1);

    #endregion

    #region IFrameSource

    /// <inheritdoc/>
    public string UiName => "Sample generator";

    /// <inheritdoc/>
    public string? Description => Invariant($"Sample generator: {_Count} frames to {(_Tcp ? "TCP" : "UDP")} {_Port}");

    /// <inheritdoc/>
    public int? EstimatedFrameCount => _Count;

    /// <inheritdoc/>
    public bool IsRunning => _Started && Volatile.Read(ref _Disposed) == 0;

    /// <inheritdoc/>
    public void Start(FrameSourceId sourceId, FrameInterfaceRegistry registry)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(registry);

        // One Ethernet interface; registering before the flag is raised makes the id visible to FrameById readers.
        _Registry = registry;
        _InterfaceId = registry.Register(sourceId, "sample0", "Sample generator interface", LinkType.Ethernet);
        _FrameIndex = 0;
        _Started = true;
    }

    /// <inheritdoc/>
    public Frame? NextFrame(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed) != 0, this);
        if (!_Started)
        {
            throw new InvalidOperationException("SampleFrameSource.Start() must be called before NextFrame().");
        }

        cancellationToken.ThrowIfCancellationRequested();

        int index = _FrameIndex;
        if (index >= _Count)
        {
            return null;
        }

        Frame frame = _Generate(index);
        _FrameIndex = index + 1;
        Interlocked.Increment(ref _ReadCount);
        return frame;
    }

    #endregion

    #region IRandomAccessFrameSource

    /// <inheritdoc/>
    public Frame? FrameById(FrameId id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _Disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Before Start there is no interface to attach frames to; ids outside 0..count-1 have no frame.
        if (!_Started)
        {
            return null;
        }

        int index = id.Value;
        if (index < 0 || index >= _Count)
        {
            return null;
        }

        return _Generate(index);
    }

    #endregion

    #region IFrameSourceStatistics

    /// <inheritdoc/>
    public int ReadFrameCount => Volatile.Read(ref _ReadCount);

    /// <inheritdoc/>
    public int SkippedFrameCount => 0;

    /// <inheritdoc/>
    public int ErrorCount => 0;

    /// <inheritdoc/>
    public bool HasErrors => false;

    #endregion

    #region Private helpers

    // Regenerates frame number index from the options; allocates the frame bytes only.
    private Frame _Generate(int index)
    {
        // The counter is a wire value: a start counter near the top of the range wraps around to zero like a real counter
        // would, so the addition is deliberately unchecked. The index is never negative here, so the cast is exact.
        ulong counter = unchecked(_StartCounter + (ulong)index);
        byte gear = (byte)(index % _GearCount);
        byte speed = SampleFrameWriter.SpeedFor(index);

        // TCP sequence numbers advance by the payload length and wrap modulo 2^32 like on the wire; one source port makes the
        // whole run one connection.
        byte[] bytes = _Tcp
            ? SampleFrameWriter.WriteTcp(counter, gear, speed, _Port, (uint)((1 + (SampleWire.PayloadLength * (long)index)) & 0xFFFFFFFF))
            : SampleFrameWriter.Write(counter, gear, speed, _Port);

        // The registry is set by Start before the started flag is raised, and every caller checked that flag.
        FrameInterfaceRegistry registry = _Registry
            ?? throw new InvalidOperationException("SampleFrameSource is not started.");
        Timestamp timestamp = new((_BaseSeconds * 1_000_000_000L) + (index * _NanosPerMilli));
        ParseResult<Frame> result = Frame.Create(new FrameId(index), timestamp, bytes, LinkType.Ethernet, _InterfaceId, registry);

        // The bytes are always a valid Ethernet frame, so a failure here is a bug in this source.
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(Invariant($"SampleFrameSource failed to create frame {index}: {result.Error}"));
    }

    #endregion
}
