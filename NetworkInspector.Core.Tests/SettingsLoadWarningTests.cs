// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsLoadWarning"/> formatting.
/// </summary>
internal sealed class SettingsLoadWarningTests
{
    [Test]
    public async Task ToString_WithSettingName_IncludesSettingAndGroup()
    {
        SettingsLoadWarning warning = new(
            SettingsLoadWarningKind.TypeMismatch,
            "mygroup",
            "test.flag",
            "Incompatible type.");

        string text = warning.ToString();

        await Assert.That(text).Contains("TypeMismatch");
        await Assert.That(text).Contains("test.flag");
        await Assert.That(text).Contains("mygroup");
        await Assert.That(text).Contains("Incompatible type.");
    }

    [Test]
    public async Task ToString_WithoutSettingName_UsesGroupOnlyFormat()
    {
        SettingsLoadWarning warning = new(
            SettingsLoadWarningKind.InvalidGroupName,
            "MyGroup",
            string.Empty,
            "Invalid group name.");

        string text = warning.ToString();

        await Assert.That(text).Contains("InvalidGroupName");
        await Assert.That(text).Contains("MyGroup");
        await Assert.That(text).Contains("Invalid group name.");
        await Assert.That(text).DoesNotContain("Setting '");
    }
}
