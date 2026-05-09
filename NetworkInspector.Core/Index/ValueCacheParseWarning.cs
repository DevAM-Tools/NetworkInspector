// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Describes a single entry that was skipped by
/// <see cref="PacketIndex.ParseValueCacheSettingValue(string?, Stack, out System.Collections.Generic.IReadOnlyList{ValueCacheParseWarning})"/>
/// together with the reason why it was skipped.
/// </summary>
/// <param name="Entry">The raw entry string that was skipped (trimmed).</param>
/// <param name="Kind">The category of the skip reason.</param>
/// <param name="Message">Human-readable description of the issue.</param>
public readonly record struct ValueCacheParseWarning(string Entry, ValueCacheParseWarningKind Kind, string Message)
{
    #region Properties

    /// <summary>
    /// Severity classification derived from <see cref="Kind"/>.
    /// <list type="bullet">
    ///   <item><c>Error</c>: configuration is malformed (<see cref="ValueCacheParseWarningKind.EmptyEntry"/>,
    ///         <see cref="ValueCacheParseWarningKind.InvalidStorageMode"/>,
    ///         <see cref="ValueCacheParseWarningKind.IncompatibleStorageMode"/>).</item>
    ///   <item><c>Warning</c>: entry refers to something the stack does not provide
    ///         (<see cref="ValueCacheParseWarningKind.UnknownField"/>,
    ///         <see cref="ValueCacheParseWarningKind.UncacheableFieldType"/>).</item>
    /// </list>
    /// </summary>
    public ValueCacheParseWarningSeverity Severity => Kind switch
    {
        ValueCacheParseWarningKind.EmptyEntry => ValueCacheParseWarningSeverity.Error,
        ValueCacheParseWarningKind.InvalidStorageMode => ValueCacheParseWarningSeverity.Error,
        ValueCacheParseWarningKind.IncompatibleStorageMode => ValueCacheParseWarningSeverity.Error,
        ValueCacheParseWarningKind.UnknownField => ValueCacheParseWarningSeverity.Warning,
        ValueCacheParseWarningKind.UncacheableFieldType => ValueCacheParseWarningSeverity.Warning,
        _ => ValueCacheParseWarningSeverity.Warning,
    };

    #endregion

    #region Formatting

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}/{Kind}] Entry '{Entry}': {Message}";

    #endregion
}
