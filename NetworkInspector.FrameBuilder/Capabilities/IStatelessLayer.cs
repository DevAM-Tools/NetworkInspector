// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Marker for layers that hold no per-frame mutable state.  An
/// <see cref="IStatelessLayer"/> instance can be reused across any number of
/// frames and threads.
/// </summary>
/// <remarks>
/// <para>
/// Cross-frame counters (IPv4 Identification, TCP sequence number, …) require
/// a separate <see cref="IStatefulLayer"/> variant; an
/// <see cref="IStatelessLayer"/> only ever produces fully-deterministic
/// header bytes from its constructor parameters and the payload it is given.
/// </para>
/// </remarks>
public interface IStatelessLayer : IProtocolLayer
{
    /// <summary>
    /// Writes this layer's header bytes into <paramref name="dst"/>.  The
    /// <paramref name="dst"/> span is exactly <see cref="IProtocolLayer.HeaderSize"/>
    /// bytes long.
    /// </summary>
    /// <remarks>
    /// Length and checksum fields must be written as zeroes here; they are
    /// patched in subsequent <see cref="FixPhase"/> walks.
    /// </remarks>
    void WriteHeader(scoped Span<byte> dst);
}

