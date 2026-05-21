// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Reassembly;

/// <summary>
/// Context information passed to PDU boundary detectors during stream reassembly.
/// Contains stream identity and protocol metadata for stateful detection decisions.
/// </summary>
public readonly struct StreamDetectionContext
{
    #region Properties

    /// <summary>Unique stream identifier (e.g., TCP stream index).</summary>
    public ulong StreamId
    {
        get; init;
    }

    /// <summary>Protocol that owns this reassembly (e.g., HTTP, DNS-over-TCP).</summary>
    public ProtocolId ProtocolId
    {
        get; init;
    }

    /// <summary>Whether the transport handshake (e.g., TCP 3-way handshake) was observed.</summary>
    public bool HandshakeObserved
    {
        get; init;
    }

    #endregion
}
