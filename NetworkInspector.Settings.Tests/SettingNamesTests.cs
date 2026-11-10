// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>The name rules of the settings and the errors that report a broken name.</summary>
internal sealed class SettingNamesTests
{
    #region Group names

    [Test]
    public async Task AGroupNameIsEmptyOrLowerCaseAndDotSeparated()
    {
        await Assert.That(SettingNames.IsValidGroupName(ReadOnlySpan<char>.Empty)).IsTrue();
        await Assert.That(SettingNames.IsValidGroupName("bad.name".AsSpan())).IsTrue();
        await Assert.That(SettingNames.IsValidGroupName("BadGroup".AsSpan())).IsFalse();
        await Assert.That(SettingNames.IsValidGroupName("not-valid".AsSpan())).IsFalse();
    }

    [Test]
    public async Task TheGroupNameDefaultIsReservedBecauseTheDefaultGroupFileHasThatName()
    {
        // The default group is saved as default.json; a group named "default" would share that file.
        await Assert.That(SettingNames.IsValidGroupName("default".AsSpan())).IsFalse();
        await Assert.That(SettingNames.IsValidGroupName("default.sub".AsSpan())).IsTrue();
        await Assert.That(SettingNames.IsValidGroupName("defaults".AsSpan())).IsTrue();
    }

    #endregion

    #region Setting names and UI names

    [Test]
    [Arguments("ip.src", true)]
    [Arguments("a.b2", true)]
    [Arguments("_x", true)]
    [Arguments("", false)]
    [Arguments("1a", false)]
    [Arguments("a..b", false)]
    [Arguments("a.", false)]
    [Arguments(".a", false)]
    [Arguments("ä", false)]
    [Arguments("a b", false)]
    public async Task ASettingNameIsADotSeparatedAsciiIdentifier(string name, bool valid)
    {
        await Assert.That(SettingNames.IsValidName(name.AsSpan())).IsEqualTo(valid);
    }

    [Test]
    public async Task AUiNameIsNonEmptyTextWithoutControlCharacters()
    {
        await Assert.That(SettingNames.IsValidUiName("Name".AsSpan())).IsTrue();
        await Assert.That(SettingNames.IsValidUiName("Größe in Bytes".AsSpan())).IsTrue();
        await Assert.That(SettingNames.IsValidUiName(ReadOnlySpan<char>.Empty)).IsFalse();
        await Assert.That(SettingNames.IsValidUiName("bad\tname".AsSpan())).IsFalse();
        await Assert.That(SettingNames.IsValidUiName("bad name!\n".AsSpan())).IsFalse();
    }

    #endregion

    #region Errors

    [Test]
    public async Task AnInvalidSettingNameIsReportedByTheSettingsErrorThatNamesIt()
    {
        InvalidNameSettingsException exception = Assert.Throws<InvalidNameSettingsException>(() => Setting.Bool("1bad", "Flag", "test", true));

        await Assert.That(exception.Name).IsEqualTo("1bad");
        await Assert.That(exception.Message).Contains("1bad");
        await Assert.That(exception).IsAssignableTo<SettingsException>();
    }

    [Test]
    public async Task AnInvalidUiNameIsReportedByItsOwnSettingsErrorThatNamesTheText()
    {
        InvalidUiNameSettingsException exception = Assert.Throws<InvalidUiNameSettingsException>(
            () => Setting.Bool("test.flag", "bad name!\n", "test", true));

        await Assert.That(exception.UiName).IsEqualTo("bad name!\n");
        await Assert.That(exception.Message).Contains("bad name!");
        await Assert.That(exception).IsAssignableTo<SettingsException>();
    }

    [Test]
    public async Task AnInvalidGroupNameIsReportedByTheSettingsErrorThatNamesTheGroup()
    {
        InvalidNameSettingsException exception = Assert.Throws<InvalidNameSettingsException>(() => Setting.Bool("test.flag", "Flag", "Bad Group", true));

        await Assert.That(exception.Name).IsEqualTo("Bad Group");
        await Assert.That(exception.Message).Contains("Bad Group");
    }

    #endregion
}
