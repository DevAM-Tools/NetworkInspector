// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="SettingsManagerFactory"/>.
/// Covers ResolvePath validation, path-traversal rejection, and Create API.
/// Not thread-safe — tests are independent and share no mutable state.
/// </summary>
internal sealed class SettingsManagerFactoryTests
{
    #region ResolvePath — null/empty profileName

    [Test]
    public async Task ResolvePath_NullSettingsPathNullProfile_ReturnsAppDataDefault()
    {
        // Arrange
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetworkInspector");

        // Act
        string result = SettingsManagerFactory.ResolvePath(null, null);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolvePath_ExplicitSettingsPathNullProfile_ReturnsSettingsPath()
    {
        // Arrange
        string basePath = Path.GetTempPath();

        // Act
        string result = SettingsManagerFactory.ResolvePath(basePath, null);

        // Assert
        await Assert.That(result).IsEqualTo(basePath);
    }

    [Test]
    public async Task ResolvePath_NullSettingsPathEmptyProfile_ReturnsAppDataDefault()
    {
        // Arrange
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetworkInspector");

        // Act
        string result = SettingsManagerFactory.ResolvePath(null, string.Empty);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolvePath_ExplicitSettingsPathEmptyProfile_ReturnsSettingsPath()
    {
        // Arrange
        string basePath = Path.GetTempPath();

        // Act
        string result = SettingsManagerFactory.ResolvePath(basePath, string.Empty);

        // Assert
        await Assert.That(result).IsEqualTo(basePath);
    }

    #endregion

    #region ResolvePath — valid profileName

    [Test]
    public async Task ResolvePath_ValidProfile_CombinesBaseAndProfile()
    {
        // Arrange
        string basePath = Path.GetTempPath();
        string profile = "profileA";

        // Act
        string result = SettingsManagerFactory.ResolvePath(basePath, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(basePath, profile));
    }

    [Test]
    public async Task ResolvePath_ProfileWithHyphenAndUnderscore_IsAccepted()
    {
        // Arrange
        string basePath = Path.GetTempPath();
        string profile = "my-profile_123";

        // Act
        string result = SettingsManagerFactory.ResolvePath(basePath, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(basePath, profile));
    }

    [Test]
    public async Task ResolvePath_NullSettingsPathWithValidProfile_CombinesDefaultAndProfile()
    {
        // Arrange
        string defaultBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetworkInspector");
        string profile = "testprofile";

        // Act
        string result = SettingsManagerFactory.ResolvePath(null, profile);

        // Assert
        await Assert.That(result).IsEqualTo(Path.Combine(defaultBase, profile));
    }

    #endregion

    #region ResolvePath — path-traversal rejection

    [Test]
    public async Task ResolvePath_ProfileWithForwardSlash_ThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(Path.GetTempPath(), "a/b"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePath_ProfileWithBackslash_ThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(Path.GetTempPath(), "a\\b"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePath_ProfileIsDotDot_ThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(Path.GetTempPath(), ".."));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    public async Task ResolvePath_ProfileContainsDotDotSegment_ThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(Path.GetTempPath(), "x..y"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    [Test]
    [Arguments("parent/../escape")]
    [Arguments("../sibling")]
    [Arguments("sub/../../escape")]
    public async Task ResolvePath_ProfileWithSlashAndDotDot_ThrowsArgumentException(string profile)
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.ResolvePath(Path.GetTempPath(), profile));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    #endregion

    #region ResolvePath — character-set contract documentation

    /// <summary>
    /// Documents the current accepted character set for profileName:
    /// alphanumeric, hyphens, underscores, and dots (but not "..").
    /// This test acts as a living contract that fails if the implementation
    /// silently accepts or rejects inputs differently than documented.
    /// </summary>
    [Test]
    [Arguments("alpha")]
    [Arguments("UPPER")]
    [Arguments("mixed123")]
    [Arguments("with-hyphen")]
    [Arguments("with_underscore")]
    [Arguments("v1.2.3")]
    public async Task ResolvePath_AcceptedProfileNames_DoNotThrow(string profile)
    {
        // Act / Assert — should not throw
        string result = SettingsManagerFactory.ResolvePath(Path.GetTempPath(), profile);
        await Assert.That(result).IsNotNull();
    }

    #endregion

    #region Create

    [Test]
    public async Task Create_NullArgs_ReturnsSettingsManagerWithDefaultPath()
    {
        // Act
        using SettingsManager manager = SettingsManagerFactory.Create();

        // Assert — SettingsManager is created without throwing
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task Create_WithExplicitSettingsPath_ReturnsSettingsManager()
    {
        // Arrange
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        using SettingsManager manager = SettingsManagerFactory.Create(tempPath);

        // Assert
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task Create_WithValidProfile_ReturnsSettingsManager()
    {
        // Arrange
        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        using SettingsManager manager = SettingsManagerFactory.Create(tempPath, "myprofile");

        // Assert
        await Assert.That(manager).IsNotNull();
    }

    [Test]
    public async Task Create_WithInvalidProfile_ThrowsArgumentException()
    {
        // Act / Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => SettingsManagerFactory.Create(Path.GetTempPath(), "bad/profile"));

        await Assert.That(ex.ParamName).IsEqualTo("profileName");
    }

    #endregion
}
