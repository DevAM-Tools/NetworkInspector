// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Sources.Blf.Format;

/// <summary>
/// Exception thrown for unrecoverable BLF format errors.
/// Used for rare, fatal problems such as invalid file magic, corrupt file headers,
/// or I/O failures during container decompression.
/// Per-object errors that may cascade from prior corruption use
/// TryXxx/bool return patterns instead.
/// </summary>
public sealed class BlfException : Exception
{
    /// <summary>Creates a new BlfException with the specified message.</summary>
    public BlfException(string message) : base(message) { }

    /// <summary>Creates a new BlfException with the specified message and inner exception.</summary>
    public BlfException(string message, Exception innerException) : base(message, innerException) { }
}
