// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Errors;

/// <summary>
/// Describes a recoverable error during export.
/// </summary>
public sealed class ExportErrorEventArgs : EventArgs
{
    #region Properties

    /// <summary>Zero-based index of the skipped frame/packet. Defaults to -1 (unknown).</summary>
    public int ItemIndex { get; init; } = -1;

    /// <summary>Category of the error.</summary>
    public ExportErrorKind Kind
    {
        get; init;
    }

    /// <summary>Human-readable error description.</summary>
    public required string Message
    {
        get; init;
    }

    #endregion
}
