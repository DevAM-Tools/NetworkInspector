// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using ConfigLoader = NetworkInspector.Protocols.Helpers.ConfigLoader;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Failures of <see cref="ConfigLoader"/> that used to escape as exceptions return an error text like every other
/// configuration failure.
/// </summary>
internal sealed class ConfigLoaderTests
{
    #region Tests

    [Test]
    public async Task LoadPathWithNulCharacterReturnsInvalidPathError()
    {
        CanConfig? config = ConfigLoader.Load("a\0b.json", CanConfigContext.Default.CanConfig, out string? error);

        await Assert.That(config).IsNull();
        await Assert.That(error).IsNotNull();
        await Assert.That(error!).StartsWith("Invalid configuration path: ");
    }

    [Test]
    public async Task LoadMissingFileReturnsNotFoundError()
    {
        string path = Path.Combine(Path.GetTempPath(), "ni-config-loader-missing-" + Guid.NewGuid().ToString("N") + ".json");

        CanConfig? config = ConfigLoader.Load(path, CanConfigContext.Default.CanConfig, out string? error);

        await Assert.That(config).IsNull();
        await Assert.That(error).IsEqualTo("Configuration file not found: " + Path.GetFullPath(path));
    }

    #endregion
}
