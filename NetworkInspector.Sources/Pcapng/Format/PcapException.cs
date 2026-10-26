// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Exception thrown for unrecoverable PCAP/PCAPNG format errors.
/// Used for rare, fatal problems found while opening a capture: an unrecognized format or magic number, a corrupt,
/// truncated, or oversized first section header, or a corrupt legacy global header. Nothing has been indexed or read
/// when it is thrown. Operating-system I/O failures are not wrapped; they propagate as <see cref="IOException"/>.
/// Per-packet errors that may cascade from prior corruption use
/// TryXxx/bool return patterns (and the error-tolerance events of the sources) instead.
/// </summary>
/// <remarks>Immutable once constructed; safe to share across threads.</remarks>
public sealed class PcapException : Exception
{
    /// <summary>Creates a new PcapException with the specified message.</summary>
    public PcapException(string message) : base(message) { }

    /// <summary>Creates a new PcapException with the specified message and inner exception.</summary>
    public PcapException(string message, Exception innerException) : base(message, innerException) { }
}
