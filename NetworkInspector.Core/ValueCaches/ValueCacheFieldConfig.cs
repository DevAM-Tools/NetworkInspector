// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Configuration for one recorded field. Payload type comes from the stack's <see cref="FieldType"/>.
/// Custom text and custom representation are extra string series on the same field id.
/// At least one of <see cref="RecordValue"/>, <see cref="RecordCustomText"/>, or
/// <see cref="RecordCustomRepresentation"/> must be true.
/// </summary>
/// <param name="FieldId">Field to record. Must be in range for the cache's stack.</param>
/// <param name="CaptureMode">How multiple occurrences in one packet are captured.</param>
/// <param name="RecordValue">When true, create a payload series for <paramref name="FieldId"/>.</param>
/// <param name="RecordCustomText">When true, create a <see cref="ValueCacheStringSeries"/> of field custom text.</param>
/// <param name="RecordCustomRepresentation">When true, create a string series of value custom representation.</param>
public readonly record struct ValueCacheFieldConfig(
    FieldId FieldId,
    ValueCaptureMode CaptureMode = ValueCaptureMode.FirstOccurrence,
    bool RecordValue = true,
    bool RecordCustomText = false,
    bool RecordCustomRepresentation = false);
