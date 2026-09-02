// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions;

/// <summary>
/// Name-based construction request for a session value cache.
/// Session validates each name with <see cref="NameValidation.IsValidName"/> (same rule as
/// stack registration), then resolves with <see cref="Stack.GetFieldId"/> /
/// <see cref="Stack.GetIndexGroupId"/> so Restart can rebind the same request to a new stack.
/// </summary>
public sealed class ValueCacheRequest
{
    /// <summary>
    /// When true, every stack field gets a payload series. Explicit <see cref="Fields"/> still add
    /// custom-text / custom-representation series and override capture mode.
    /// </summary>
    public bool RecordAllFields { get; init; }

    /// <summary>
    /// Capture mode used for <see cref="FieldNames"/> shorthand, <see cref="GroupNames"/> expansion,
    /// and <see cref="RecordAllFields"/> payload series.
    /// </summary>
    public ValueCaptureMode DefaultCaptureMode { get; init; } = ValueCaptureMode.FirstOccurrence;

    /// <summary>Shorthand: each name becomes a payload series with <see cref="DefaultCaptureMode"/>.</summary>
    public IReadOnlyList<string> FieldNames { get; init; } = [];

    /// <summary>
    /// Index-group names expanded to payload-only series with <see cref="DefaultCaptureMode"/>.
    /// An explicit <see cref="Fields"/> entry for the same field wins.
    /// </summary>
    public IReadOnlyList<string> GroupNames { get; init; } = [];

    /// <summary>
    /// Explicit fields including CustomText / CustomRepresentation flags.
    /// A <see cref="Fields"/> entry for the same name overrides the shorthand <see cref="FieldNames"/> row.
    /// </summary>
    public IReadOnlyList<ValueCacheFieldRequest> Fields { get; init; } = [];

    /// <summary>Optional row and byte bounds. Default is unlimited.</summary>
    public ValueCacheLimits Limits { get; init; } = ValueCacheLimits.Unlimited;
}
