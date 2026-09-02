// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.ValueCaches;

/// <summary>
/// Construction options for <see cref="ValueCache"/>.
/// <see cref="RecordAllFields"/> creates a payload series for every stack field (including
/// <see cref="FieldType.None"/> as packet-id presence) and does not auto-create custom-text or
/// custom-representation series. Explicit field configs add those series and override capture mode.
/// </summary>
public sealed class ValueCacheBuildOptions
{
    /// <summary>When true, every <see cref="Stack.Fields"/> entry gets a payload series.</summary>
    public bool RecordAllFields { get; init; }

    /// <summary>Capture mode used for <see cref="RecordAllFields"/> payload series and for group expansion defaults.</summary>
    public ValueCaptureMode DefaultCaptureMode { get; init; } = ValueCaptureMode.FirstOccurrence;

    /// <summary>Optional row and byte bounds. Default is <see cref="ValueCacheLimits.Unlimited"/>.</summary>
    public ValueCacheLimits Limits { get; init; } = ValueCacheLimits.Unlimited;
}
