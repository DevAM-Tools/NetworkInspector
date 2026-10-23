// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The rules for plugin info and registration names: the only length limit is the name, texts may have any length.</summary>
internal sealed class PluginInfoRulesTests
{
    #region Helpers

    private static PluginInfo _Info(string name = "Valid.Name") => new(name, "1.0.0", "MIT", "A plugin.", []);

    #endregion

    #region Names

    [Test]
    [Arguments("Sample.Protocol")]
    [Arguments("a")]
    [Arguments("_x.y_2")]
    public async Task ValidNamesAreAccepted(string name)
    {
        bool valid = PluginInfoRules.TryValidate(_Info(name), out string? problem);

        await Assert.That(valid).IsTrue();
        await Assert.That(problem).IsNull();
    }

    [Test]
    public async Task ANameOf256CharactersIsAcceptedAnd257IsRejected()
    {
        string longest = "a" + new string('b', 255);

        await Assert.That(PluginInfoRules.TryValidate(_Info(longest), out _)).IsTrue();
        await Assert.That(PluginInfoRules.TryValidate(_Info(longest + "b"), out string? problem)).IsFalse();
        await Assert.That(Expect.NotNull(problem)).Contains("more than 256 characters");
    }

    [Test]
    [Arguments("bad name")]
    [Arguments("1abc")]
    [Arguments("a..b")]
    [Arguments("a.")]
    [Arguments("")]
    public async Task InvalidNamesAreRejected(string name)
    {
        bool valid = PluginInfoRules.TryValidate(_Info(name), out string? problem);

        await Assert.That(valid).IsFalse();
        await Assert.That(problem).IsNotNull();
    }

    #endregion

    #region Texts and dependencies

    [Test]
    public async Task EmptyTextsOfAnyLengthAreAccepted()
    {
        PluginInfo empty = new("A.B", string.Empty, string.Empty, string.Empty, []);
        PluginInfo long1 = new("A.B", new string('v', 100_000), new string('l', 100_000), new string('d', 100_000), []);

        await Assert.That(PluginInfoRules.TryValidate(empty, out _)).IsTrue();
        await Assert.That(PluginInfoRules.TryValidate(long1, out _)).IsTrue();
    }

    [Test]
    public async Task NullTextsAndNullDependenciesAreRejected()
    {
        PluginInfo nullVersion = new("A.B", null!, "MIT", "d", []); // deliberate nulls: the rules must catch what a plugin returns
        PluginInfo nullLicense = new("A.B", "1", null!, "d", []);
        PluginInfo nullDescription = new("A.B", "1", "MIT", null!, []);
        PluginInfo nullList = new("A.B", "1", "MIT", "d", null!);
        PluginInfo nullEntry = new("A.B", "1", "MIT", "d", [null!]);
        PluginInfo nullField = new("A.B", "1", "MIT", "d", [new PluginDependency("n", "v", "l", null!, "link", "text")]);

        foreach (PluginInfo info in new[] { nullVersion, nullLicense, nullDescription, nullList, nullEntry, nullField })
        {
            await Assert.That(PluginInfoRules.TryValidate(info, out string? problem)).IsFalse();
            await Assert.That(problem).IsNotNull();
        }

        await Assert.That(PluginInfoRules.TryValidate(null, out _)).IsFalse();
    }

    [Test]
    public async Task ACompleteDependencyIsAccepted()
    {
        PluginInfo info = new("A.B", "1", "MIT", "d", [new PluginDependency("Lib", "2.0", "Apache-2.0", "Holder", "https://example.invalid", "text")]);

        await Assert.That(PluginInfoRules.TryValidate(info, out _)).IsTrue();
    }

    #endregion

    #region Registration names

    [Test]
    [Arguments("BLF", true)]
    [Arguments("a_b.c-d", true)]
    [Arguments("a b", false)]
    [Arguments("1x", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task RegistrationNamesStartWithALetterAndHaveNoSpaces(string? name, bool expected)
    {
        await Assert.That(PluginInfoRules.IsValidRegistrationName(name)).IsEqualTo(expected);
    }

    [Test]
    public async Task RegistrationNamesAcceptTwoHundredFiftySixCharactersAndRefuseMore()
    {
        await Assert.That(PluginInfoRules.IsValidRegistrationName("k" + new string('x', 255))).IsTrue();
        await Assert.That(PluginInfoRules.IsValidRegistrationName("k" + new string('x', 256))).IsFalse();
    }

    #endregion

    #region Loaded plugin

    [Test]
    public async Task APluginWithAnInvalidNameIsFailedWithAnInfoReason()
    {
        using LoaderHarness harness = new("info-rules");
        string folder = harness.Folder.FilePath("plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.TestPlugins, folder);

        PluginLoadReport report = harness.Load([folder]);

        PluginRecord invalid = report.Plugins.Single(static record => record.Name.EndsWith("InvalidInfoPlugin", StringComparison.Ordinal));
        await Assert.That(invalid.State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(Expect.NotNull(invalid.Reason)).StartsWith("invalid plugin info:");
    }

    #endregion
}
