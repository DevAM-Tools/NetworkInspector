// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

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

    /// <summary>
    /// The capture file changed (length or modification time) while the source was reading it. The source stops: no
    /// further frames are returned and <c>FrameById</c> returns <c>null</c>. Detected on Linux and macOS; Windows
    /// denies writers while the source is open. To read the new content, dispose the source and open the file again.
    /// </summary>
    SourceFileChanged,

    #endregion
}
