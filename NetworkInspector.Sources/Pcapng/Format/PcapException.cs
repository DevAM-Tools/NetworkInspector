// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Exception thrown for unrecoverable PCAP/PCAPNG format errors.
/// Used for rare, fatal problems such as unrecognized magic numbers,
/// corrupt section headers, or I/O failures.
/// Per-packet errors that may cascade from prior corruption use
/// TryXxx/bool return patterns instead.
/// </summary>
public sealed class PcapException : Exception
{
    /// <summary>Creates a new PcapException with the specified message.</summary>
    public PcapException(string message) : base(message) { }

    /// <summary>Creates a new PcapException with the specified message and inner exception.</summary>
    public PcapException(string message, Exception innerException) : base(message, innerException) { }
}
