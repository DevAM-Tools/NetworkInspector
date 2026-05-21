// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Describes where a single frame sits within the TCP stream generation layout.
/// </summary>
/// <param name="StreamIndex">Zero-based index of the TCP connection.</param>
/// <param name="LocalFrameIndex">Frame position within that connection (0-based).</param>
internal readonly record struct TcpFrameLocation(int StreamIndex, int LocalFrameIndex);

/// <summary>
/// Precomputes the deterministic frame layout for TCP stream generation.
/// <para>
/// Given <see cref="TcpStreamOptions"/>, this calculates the exact number of frames
/// per connection and the total frame count across all connections. The <see cref="Locate"/>
/// method maps any global frame index to a <see cref="TcpFrameLocation"/> (stream + local index)
/// in O(1), enabling fully stateless random-access frame generation.
/// </para>
/// <para>
/// All connections share the same structure (same segment count, same handshake/teardown flags),
/// making the layout uniform and trivially indexable.
/// </para>
/// </summary>
internal readonly struct TcpStreamLayout
{
    #region Properties

    /// <summary>Number of handshake frames per connection (0 or 3).</summary>
    internal int HandshakeFrames
    {
        get;
    }

    /// <summary>Number of data frames per connection.</summary>
    internal int DataFrames
    {
        get;
    }

    /// <summary>Number of teardown frames per connection (0 or 4).</summary>
    internal int TeardownFrames
    {
        get;
    }

    /// <summary>Total frames per connection (handshake + data + teardown).</summary>
    internal int FramesPerConnection
    {
        get;
    }

    /// <summary>Number of concurrent TCP connections.</summary>
    internal int StreamCount
    {
        get;
    }

    /// <summary>Total frames across all connections.</summary>
    internal int TotalFrameCount
    {
        get;
    }

    /// <summary>Whether frames are interleaved round-robin across connections.</summary>
    internal bool Interleaved
    {
        get;
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Creates a layout from the given TCP stream options.
    /// </summary>
    internal TcpStreamLayout(TcpStreamOptions options)
    {
        HandshakeFrames = options.IncludeHandshake ? 3 : 0;
        DataFrames = options.SegmentsPerStream;
        TeardownFrames = options.IncludeTeardown ? 4 : 0;
        FramesPerConnection = HandshakeFrames + DataFrames + TeardownFrames;
        StreamCount = options.StreamCount;
        TotalFrameCount = FramesPerConnection * StreamCount;
        Interleaved = options.InterleaveStreams;
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Maps a global frame index to a (stream, local position) pair.
    /// <para>
    /// <b>Sequential mode:</b> connections are generated one after another.<br/>
    /// <c>streamIndex = globalIndex / FramesPerConnection</c><br/>
    /// <c>localIndex  = globalIndex % FramesPerConnection</c>
    /// </para>
    /// <para>
    /// <b>Interleaved mode:</b> frames round-robin across all connections.<br/>
    /// <c>round       = globalIndex / StreamCount</c><br/>
    /// <c>streamIndex = globalIndex % StreamCount</c><br/>
    /// <c>localIndex  = round</c>
    /// </para>
    /// </summary>
    /// <param name="globalIndex">Zero-based index within the entire frame sequence.</param>
    /// <returns>The stream and local position, or <c>null</c> if the index is out of range.</returns>
    internal TcpFrameLocation? Locate(int globalIndex)
    {
        if (globalIndex < 0 || globalIndex >= TotalFrameCount)
        {
            return null;
        }

        if (Interleaved)
        {
            int round = globalIndex / StreamCount;
            int streamIndex = globalIndex % StreamCount;
            return new TcpFrameLocation(streamIndex, round);
        }
        else
        {
            int streamIndex = globalIndex / FramesPerConnection;
            int localIndex = globalIndex % FramesPerConnection;
            return new TcpFrameLocation(streamIndex, localIndex);
        }
    }

    /// <summary>
    /// Classifies a local frame index into its TCP phase.
    /// </summary>
    internal TcpFramePhase ClassifyPhase(int localIndex)
    {
        if (localIndex < HandshakeFrames)
        {
            return TcpFramePhase.Handshake;
        }

        if (localIndex < HandshakeFrames + DataFrames)
        {
            return TcpFramePhase.Data;
        }

        return TcpFramePhase.Teardown;
    }

    /// <summary>
    /// Returns the sub-step within the current phase.
    /// For handshake: 0=SYN, 1=SYN-ACK, 2=ACK.
    /// For data: 0-based data segment index.
    /// For teardown: 0=FIN-ACK(client), 1=ACK(server), 2=FIN-ACK(server), 3=final ACK(client).
    /// </summary>
    internal int PhaseStep(int localIndex)
    {
        if (localIndex < HandshakeFrames)
        {
            return localIndex;
        }

        if (localIndex < HandshakeFrames + DataFrames)
        {
            return localIndex - HandshakeFrames;
        }

        return localIndex - HandshakeFrames - DataFrames;
    }

    #endregion
}

/// <summary>
/// Identifies which phase a TCP frame belongs to.
/// </summary>
internal enum TcpFramePhase
{
    #region Enum Values

    /// <summary>Three-way handshake (SYN, SYN-ACK, ACK).</summary>
    Handshake,

    /// <summary>Data transfer segments.</summary>
    Data,

    /// <summary>Connection teardown (FIN-ACK, ACK, FIN-ACK, ACK).</summary>
    Teardown,

    #endregion
}
