// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Name-based field configuration for a <see cref="ValueCacheRequest"/>.
/// Session resolves <see cref="FieldName"/> against the current stack at construction and on Restart.
/// The request holds names, not field ids, because ids belong to one stack and Restart rebinds the same request to
/// the new stack. At least one of <see cref="RecordValue"/>, <see cref="RecordCustomText"/> and
/// <see cref="RecordCustomRepresentation"/> must be <see langword="true"/>; the value cache rejects a field that
/// records nothing, and the session reports that as <see cref="SessionErrorCode.ValueCacheInvalidRequest"/>.
/// </summary>
public sealed class ValueCacheFieldRequest
{
    #region Properties

    /// <summary>
    /// Ordinal field name as registered on the stack (for example <c>udp.srcport</c>).
    /// Must pass <see cref="NameValidation.IsValidName"/> when Session binds the request.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>Capture mode for this field. Defaults to first occurrence per packet.</summary>
    public ValueCaptureMode CaptureMode { get; init; } = ValueCaptureMode.FirstOccurrence;

    /// <summary>When true, create a payload series for the resolved field.</summary>
    public bool RecordValue { get; init; } = true;

    /// <summary>When true, create a custom-text series for the resolved field.</summary>
    public bool RecordCustomText { get; init; }

    /// <summary>When true, create a custom-representation series for the resolved field.</summary>
    public bool RecordCustomRepresentation { get; init; }

    #endregion
}
