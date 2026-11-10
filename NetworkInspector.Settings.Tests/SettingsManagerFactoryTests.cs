// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// Tests for <see cref="SettingsManagerFactory"/>.
/// Covers ResolvePath validation, path-traversal rejection, and Create API.
/// Not thread-safe — tests are independent and share no mutable state.
/// </summary>
internal sealed class SettingsManagerFactoryTests
{
    #region ResolvePath — null/empty profileName

    [Test]
    public async Task ResolvePathNullSettingsPathNullProfileReturnsAppDataDefault()
    {
        // Arrange
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TestApp");

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, null, null);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolvePathExplicitSettingsPathNullProfileReturnsSettingsPath()
    {
        // Arrange
        string basePath = Path.GetTempPath();

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, null);

        // Assert
        await Assert.That(result).IsEqualTo(basePath);
    }

    [Test]
    public async Task ResolvePathNullSettingsPathEmptyProfileReturnsAppDataDefault()
    {
        // Arrange
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TestApp");

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, null, string.Empty);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolvePathExplicitSettingsPathEmptyProfileReturnsSettingsPath()
    {
        // Arrange
        string basePath = Path.GetTempPath();

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, string.Empty);

        // Assert
        await Assert.That(result).IsEqualTo(basePath);
    }

    #endregion

    #region ResolvePath — valid profileName

    [Test]
    public async Task ResolvePathValidProfileCombinesBaseAndProfile()
    {
        // Arrange
        string basePath = Path.GetTempPath();
        string profile = "profileA";

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(basePath, profile));
    }

    [Test]
    public async Task ResolvePathProfileWithHyphenAndUnderscoreIsAccepted()
    {
        // Arrange
        string basePath = Path.GetTempPath();
        string profile = "my-profile_123";

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, basePath, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(basePath, profile));
    }

    [Test]
    public async Task ResolvePathNullSettingsPathWithValidProfileCombinesDefaultAndProfile()
    {
        // Arrange
        string defaultBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TestApp");
        string profile = "testprofile";

        // Act
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, null, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(defaultBase, profile));
    }

    #endregion

    #region ResolvePath — path-traversal rejection

    [Test]
    public async Task ResolvePathProfileWithForwardSlashThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), "a/b"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePathProfileWithBackslashThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), "a\\b"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePathProfileIsDotDotThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), ".."));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePathProfileWithInnerDotsIsAnOrdinaryFolderName()
    {
        // "x..y" is not a path traversal: only a whole ".." component is, and the leading dot is refused by the profile rule.
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), "x..y");

        await Assert.That(result).IsEqualTo(Path.Combine(Path.GetTempPath(), "x..y"));
    }

    [Test]
    [Arguments("parent/../escape")]
    [Arguments("../sibling")]
    [Arguments("sub/../../escape")]
    public async Task ResolvePathProfileWithSlashAndDotDotThrowsArgumentException(string profile)
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), profile));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    #endregion

    #region ResolvePath — character-set contract documentation

    /// <summary>
    /// Documents the current accepted character set for profileName:
    /// ASCII letters, digits, spaces, dots, hyphens, and underscores (see ProfileNames).
    /// This test acts as a living contract that fails if the implementation
    /// silently accepts or rejects inputs differently than documented.
    /// </summary>
    [Test]
    [Arguments("alpha")]
    [Arguments("UPPER")]
    [Arguments("mixed123")]
    [Arguments("with-hyphen")]
    [Arguments("with_underscore")]
    [Arguments("foo bar")]
    [Arguments("v1.2.3")]
    public async Task ResolvePathAcceptedProfileNamesDoNotThrow(string profile)
    {
        // Act / Assert — should not throw
        string result = SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), profile);
        await Assert.That(result).IsNotNull();
    }

    [Test]
    [Arguments("foo@bar")]
    [Arguments("foo:bar")]
    [Arguments("tab	here")]
    public async Task ResolvePathProfileWithDisallowedCharactersThrowsArgumentException(string profile)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(TestLayout.Default, Path.GetTempPath(), profile));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    #endregion

    #region Create

    [Test]
    public async Task CreateNullArgsReturnsSettingsManagerWithDefaultPath()
    {
        // Act
        using SettingsManager manager = SettingsManagerFactory.Create(TestLayout.Default);

        // Assert — SettingsManager is created without throwing
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task CreateWithExplicitSettingsPathReturnsSettingsManager()
    {
        // Arrange
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(null, CultureInfo.InvariantCulture));

        // Act
        using SettingsManager manager = SettingsManagerFactory.Create(TestLayout.Default, tempPath);

        // Assert
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task CreateWithValidProfileReturnsSettingsManager()
    {
        // Arrange
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(null, CultureInfo.InvariantCulture));

        // Act
        using SettingsManager manager = SettingsManagerFactory.Create(TestLayout.Default, tempPath, "myprofile");

        // Assert
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task CreateWithInvalidProfileThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.Create(TestLayout.Default, Path.GetTempPath(), "bad/profile"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task CreateOmittingLimitsUsesTheSecureDefaults()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using SettingsManager manager = SettingsManagerFactory.Create(TestLayout.Default, tempPath);

        await Assert.That(manager.Limits).IsEqualTo(SettingsLimitValues.Default);
    }

    [Test]
    public async Task CreateRefusesAZeroLimit()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        SettingsLimitValues limits = new() { MaxFileSize = LimitValue.Of(0) };

        await Assert.That(() => SettingsManagerFactory.Create(TestLayout.Default, tempPath, "p", limits)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CreateCustomLimitsAreForwardedToTheManager()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        SettingsLimitValues limits = new() { MaxFileSize = LimitValue.Of(4096), MaxJsonDepth = LimitValue.Off };

        using SettingsManager manager = SettingsManagerFactory.Create(TestLayout.Default, tempPath, "p", limits);

        await Assert.That(manager.Limits).IsEqualTo(limits);
    }

    #endregion
}
