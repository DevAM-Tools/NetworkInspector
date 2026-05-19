// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Categories of recoverable export errors.
/// </summary>
public enum ExportErrorKind
{
    #region Enum Values

    /// <summary>The frame's link type is not supported by this exporter.</summary>
    UnsupportedType,

    /// <summary>The frame/packet data is malformed and could not be serialized.</summary>
    MalformedData,

    /// <summary>A serialization sub-step failed (e.g., NaN in JSON).</summary>
    SerializationError,

    /// <summary>Compression failed but uncompressed fallback was used.</summary>
    CompressionFallback,

    /// <summary>An I/O error occurred during write (e.g., disk full, stream closed).</summary>
    IoError,

    /// <summary>Other recoverable error.</summary>
    Other,

    #endregion
}