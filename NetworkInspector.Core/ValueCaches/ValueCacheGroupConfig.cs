// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Configuration expanded at construction to every stack field whose index group equals <see cref="GroupId"/>.
/// An explicit <see cref="ValueCacheFieldConfig"/> for the same field wins for a given series.
/// </summary>
/// <param name="GroupId">Index group to expand. Must be in range for the cache's stack.</param>
/// <param name="CaptureMode">Capture mode applied to expanded series unless an explicit field config already created that series.</param>
/// <param name="RecordValue">When true, create a payload series for each member field that does not already have one.</param>
/// <param name="RecordCustomText">When true, create custom-text series for member fields that do not already have one.</param>
/// <param name="RecordCustomRepresentation">When true, create custom-representation series for member fields that do not already have one.</param>
public readonly record struct ValueCacheGroupConfig(
    IndexGroupId GroupId,
    ValueCaptureMode CaptureMode = ValueCaptureMode.FirstOccurrence,
    bool RecordValue = true,
    bool RecordCustomText = false,
    bool RecordCustomRepresentation = false);
