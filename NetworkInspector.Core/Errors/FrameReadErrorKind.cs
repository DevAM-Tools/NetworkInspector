// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Categories of recoverable frame read errors.
/// </summary>
public enum FrameReadErrorKind
{
    #region Enum Values

    /// <summary>The frame's data block is corrupted or truncated.</summary>
    CorruptedBlock,

    /// <summary>Decompression of a container or block failed.</summary>
    DecompressionFailure,

    /// <summary>The frame references an interface that was not registered.</summary>
    UnresolvedInterface,

    /// <summary>The frame header could not be parsed.</summary>
    MalformedHeader,

    /// <summary>A required checksum validation failed.</summary>
    ChecksumMismatch,

    /// <summary>Other recoverable error.</summary>
    Other,

    /// <summary>The stream was truncated mid-block or mid-frame.</summary>
    TruncatedStream,

    #endregion
}