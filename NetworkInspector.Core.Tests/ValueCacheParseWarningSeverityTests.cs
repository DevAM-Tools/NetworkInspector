// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="ValueCacheParseWarning.Severity"/> (F-IX-06): maps each
/// <see cref="ValueCacheParseWarningKind"/> to the expected severity bucket.
/// </summary>
internal sealed class ValueCacheParseWarningSeverityTests
{
    [Test]
    public async Task Severity_EmptyEntry_IsError()
    {
        ValueCacheParseWarning w = new("", ValueCacheParseWarningKind.EmptyEntry, "msg");
        await Assert.That(w.Severity).IsEqualTo(ValueCacheParseWarningSeverity.Error);
    }

    [Test]
    public async Task Severity_InvalidStorageMode_IsError()
    {
        ValueCacheParseWarning w = new("x:bad", ValueCacheParseWarningKind.InvalidStorageMode, "msg");
        await Assert.That(w.Severity).IsEqualTo(ValueCacheParseWarningSeverity.Error);
    }

    [Test]
    public async Task Severity_IncompatibleStorageMode_IsError()
    {
        ValueCacheParseWarning w = new("x:int8",
            ValueCacheParseWarningKind.IncompatibleStorageMode, "msg");
        await Assert.That(w.Severity).IsEqualTo(ValueCacheParseWarningSeverity.Error);
    }

    [Test]
    public async Task Severity_UnknownField_IsWarning()
    {
        ValueCacheParseWarning w = new("foo", ValueCacheParseWarningKind.UnknownField, "msg");
        await Assert.That(w.Severity).IsEqualTo(ValueCacheParseWarningSeverity.Warning);
    }

    [Test]
    public async Task Severity_UncacheableFieldType_IsWarning()
    {
        ValueCacheParseWarning w = new("x", ValueCacheParseWarningKind.UncacheableFieldType, "msg");
        await Assert.That(w.Severity).IsEqualTo(ValueCacheParseWarningSeverity.Warning);
    }

    [Test]
    public async Task ToString_IncludesSeverityAndKind()
    {
        ValueCacheParseWarning w = new("foo", ValueCacheParseWarningKind.UnknownField, "not registered");
        string s = w.ToString();
        await Assert.That(s).Contains("Warning");
        await Assert.That(s).Contains("UnknownField");
        await Assert.That(s).Contains("foo");
        await Assert.That(s).Contains("not registered");
    }
}
