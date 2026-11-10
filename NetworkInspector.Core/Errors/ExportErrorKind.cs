// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Categories of recoverable export errors.
/// </summary>
public enum ExportErrorKind
{
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

    /// <summary>
    /// A packet was parsed on another protocol stack than the one the export's catalog was built from (a session restart
    /// replaced the stack mid-export). A catalog exporter (PBF columnar, Parquet, DuckDB) refuses the packet instead of writing
    /// field ids that name other fields in its catalog; the export stops and the earlier packets stay a valid dataset. Start a
    /// new export on the new stack. Appended after the earlier members so their numbers stay stable.
    /// </summary>
    StackMismatch,
}
