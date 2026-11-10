// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>The code of the package README and of the guide, run as tests so that the documented example cannot go stale.</summary>
/// <remarks>
/// Sync: <c>NetworkInspector.Settings/README.md</c> § Quickstart and <c>docs/guides/settings.md</c> § Add settings to an application
/// show the body of <see cref="TheQuickstartWritesTheDocumentedOutput"/> line by line; only its first line differs (the documents
/// write to <see cref="Console.Out"/>, the test to a <see cref="StringWriter"/>). A change of the snippet is made in the test and
/// both documents together.
/// </remarks>
internal sealed class GuideSnippetTests
{
    [Test]
    public async Task TheQuickstartWritesTheDocumentedOutput()
    {
        StringWriter output = new();

        // 1. Choose the names of your application once: the folder under the application data folder, the folders next to the
        //    profiles that are no profiles, and the two bookkeeping files of a settings folder. Never change them between releases.
        SettingsStorageLayout layout = SettingsStorageLayout.Create("Alpha", ["logs", "cache"], ".alpha.lock", ".alpha-owned");

        // A limit is declared once (see the guide of NetworkInspector.Limits) and registered like any other setting.
        LimitDefinition maxEntrySize = new(
            "app.maxEntrySize",
            LimitUnits.Bytes,
            defaultValue: 128L * 1024 * 1024,
            minimum: 1024,
            maximum: 1L << 40,
            new LimitTexts(
                Title: "Largest archive entry",
                Description: "The largest size of one entry that is read from an archive.",
                Reason: "A hostile archive could otherwise claim an entry that uses all memory.",
                Benefit: "A broken or hostile archive is refused with a clear message.",
                SmallerValues: "Entries above the value are refused: 1 MiB refuses a 2 MiB entry.",
                LargerValues: "More memory may be used per entry: 1 GiB lets a 900 MiB entry through.",
                OffEffect: "Every entry is accepted and a hostile archive can use all memory."));

        // 2. Create a manager for one folder, register the settings, and load what an earlier run stored.
        string folder = Path.Combine(Path.GetTempPath(), "settings-quickstart-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        using (SettingsManager manager = new(folder, layout))
        {
            SettingsRegistrar registrar = new(manager);
            registrar.RegisterStringSetting("app.greeting", "Greeting", "app", "Hello");
            registrar.RegisterLimit(maxEntrySize);
            manager.Load();

            // 3. Change values: a pending value becomes the current value with ApplyChanges, and Save writes the changed values.
            Setting greeting = manager.GetSetting("app.greeting")!;
            Setting limit = manager.GetSetting("app.maxEntrySize")!;
            greeting.SetPendingValue(SettingValue.String("Hi"));
            if (SettingValueText.TryParse(limit, "256 MiB", out SettingValue chosen, out SettingProblem? problem))
            {
                limit.SetPendingValue(chosen);
            }
            else
            {
                output.WriteLine(problem);
            }

            manager.ApplyChanges();
            manager.Save();
        }

        // 4. A new manager, for example in the next run, reads the same values.
        using (SettingsManager later = new(folder, layout))
        {
            SettingsRegistrar registrar = new(later);
            registrar.RegisterStringSetting("app.greeting", "Greeting", "app", "Hello");
            registrar.RegisterLimit(maxEntrySize);
            later.Load();

            output.WriteLine(later.GetStringSetting("app.greeting"));
            output.WriteLine(maxEntrySize.Format(later.GetLimit("app.maxEntrySize")!.Value));

            // A hot loop keeps the live holder of the limit and reads its ceiling: no lookup, no lock, no allocation.
            Limit live = later.GetLiveLimit("app.maxEntrySize")!;
            output.WriteLine(live.Ceiling);
        }

        // 5. The folder holds one file per group and the two bookkeeping files of the layout.
        foreach (string path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            output.WriteLine(Path.GetFileName(path));
        }

        Directory.Delete(folder, recursive: true);

        await Assert.That(output.ToString()).IsEqualTo(_Documented);
    }

    [Test]
    public async Task TheFailureTextsOfTheGuideAreTheTextsTheLibraryProduces()
    {
        Setting port = Setting.I64("app.port", "Port", "app", 8080, min: 1, max: 65535);
        Setting level = Setting.Bool("app.verbose", "Verbose", "app", false);
        LimitDefinition limit = LimitTestDefinitions.Bytes(key: "app.maxEntrySize", minimum: 1024, maximum: 1L << 40);
        Setting size = Setting.FromLimit(limit);

        SettingValueText.TryParse(port, "abc", out _, out SettingProblem? notANumber);
        SettingValueText.TryParse(port, "70000", out _, out SettingProblem? outOfRange);
        SettingValueText.TryParse(level, "maybe", out _, out SettingProblem? notABool);
        SettingValueText.TryParse(size, "256 MB", out _, out SettingProblem? decimalUnit);

        await Assert.That(notANumber!.ToString()).IsEqualTo("app.port: 'abc' is not a whole number; example: 8080");
        await Assert.That(outOfRange!.ToString()).IsEqualTo("app.port: allowed 1 – 65535");
        await Assert.That(notABool!.ToString()).IsEqualTo("app.verbose: 'maybe' is not true or false");
        await Assert.That(decimalUnit!.ToString()).IsEqualTo("app.maxEntrySize: 'MB' is a decimal unit and is not accepted; use 256 MiB");
    }

    /// <summary>The limit fragment of the guide (<c>docs/guides/settings.md</c> § Choose the limits of the loader), run as a test.</summary>
    [Test]
    public async Task TheLimitFragmentOfTheGuideCreatesAManagerWithTheChangedLimitAndTheOtherDefaults()
    {
        string folder = Path.Combine(Path.GetTempPath(), "settings-limits-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        SettingsStorageLayout layout = TestLayout.Default;

        SettingsLimitValues limits = SettingsLimitValues.Default with { MaxFileSize = LimitValue.Of(8L << 20) };
        using SettingsManager manager = new(folder, layout, limits);

        await Assert.That(SettingsLimits.MaxFileSize.Format(manager.Limits.MaxFileSize)).IsEqualTo("8 MiB");
        await Assert.That(manager.Limits.MaxJsonDepth).IsEqualTo(SettingsLimits.MaxJsonDepth.DefaultValue);
        await Assert.That(SettingsLimits.FolderLockTimeout.Format(manager.Limits.FolderLockTimeout)).IsEqualTo("2 s");
    }

    /// <summary>The output block of the README quickstart and of the guide.</summary>
    private static readonly string _Documented = string.Join(
        Environment.NewLine,
        "Hi",
        "256 MiB",
        "268435456",
        ".alpha-owned",
        ".alpha.lock",
        "app.json",
        string.Empty);
}
