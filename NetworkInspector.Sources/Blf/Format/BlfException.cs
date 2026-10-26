// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Exception thrown for BLF format errors that cannot be repaired locally.
/// Used for rare problems such as invalid file magic, corrupt file headers,
/// or a container payload that cannot be decompressed (unknown method, corrupt stream, size mismatch).
/// Per-object errors that may cascade from prior corruption use
/// TryXxx/bool return patterns instead.
/// </summary>
/// <remarks>
/// Whether such an error is fatal is decided by the reader, not by the type: the scanner and the sources catch it per
/// container, count it, and continue with the next container (the error tolerance mode decides whether that aborts).
/// Deliberately a different type than <see cref="BlfDecompressionLimitExceededException"/>, which must not be swallowed
/// by those catch blocks. Thread-safety: exceptions are not shared mutable state; nothing to synchronise.
/// </remarks>
public sealed class BlfException : Exception
{
    /// <summary>Creates a new BlfException with the specified message.</summary>
    public BlfException(string message) : base(message) { }

    /// <summary>Creates a new BlfException with the specified message and inner exception.</summary>
    public BlfException(string message, Exception innerException) : base(message, innerException) { }
}
