// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Context information passed to PDU boundary detectors during stream reassembly.
/// Contains stream identity and protocol metadata for stateful detection decisions.
/// Passed by <c>in</c> reference so the per-call context costs no copy or allocation on the reassembly path.
/// </summary>
public readonly record struct StreamDetectionContext
{
    /// <summary>Unique stream identifier (e.g., TCP stream index).</summary>
    public ulong StreamId { get; init; }

    /// <summary>Protocol that owns this reassembly (e.g., HTTP, DNS-over-TCP).</summary>
    public ProtocolId ProtocolId { get; init; }

    /// <summary>Whether the transport handshake (e.g., TCP 3-way handshake) was observed.</summary>
    public bool HandshakeObserved { get; init; }

    /// <summary>
    /// Direction of the buffered bytes relative to the normalized stream key (<see langword="true"/> for the direction of the
    /// lower endpoint). Lets a stateful detector keep one cursor per direction and pair what one direction sent with what
    /// the other answered (for example the request that a response belongs to).
    /// </summary>
    public bool IsForward { get; init; }

    /// <summary>
    /// The transport signalled that the sender will send no more bytes in this direction (for example a TCP FIN). A detector for a format
    /// whose end is "until the connection closes" (a body without length) may then report the buffered bytes as complete;
    /// while this is <see langword="false"/> it must report such a message as incomplete.
    /// </summary>
    public bool StreamClosed { get; init; }
}
