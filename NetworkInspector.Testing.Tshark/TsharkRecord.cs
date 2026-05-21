// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Per-packet record returned by capture-file verification helpers (used by exporter
/// roundtrip tests).
/// </summary>
/// <param name="FrameNumber">1-based frame index inside the capture.</param>
/// <param name="TimeEpochNanos">Capture timestamp in nanoseconds since the Unix epoch.</param>
/// <param name="FrameLen">Original frame length in bytes (uncaptured length).</param>
/// <param name="InterfaceName">Interface display name reported by tshark.</param>
/// <param name="InterfaceId">0-based interface ordinal inside the capture.</param>
/// <param name="EncapType">Wireshark <c>WTAP_ENCAP_*</c> identifier reported by tshark.</param>
/// <remarks>
/// Byte-exact frame content is intentionally not part of this record. tshark does not
/// surface a generic per-frame raw-bytes field through <c>-T fields</c>; raw-content
/// verification is done in-process by the consumer (for example the exporter roundtrip
/// reimport step).
/// </remarks>
public sealed record TsharkRecord(
    int FrameNumber,
    long TimeEpochNanos,
    int FrameLen,
    string InterfaceName,
    int InterfaceId,
    int EncapType);
