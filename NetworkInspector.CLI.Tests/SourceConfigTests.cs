// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Source specs resolved through the plugin registry: kinds, paths, option texts, legacy spellings, and errors.</summary>
internal sealed class SourceConfigTests
{
    #region Helpers

    private static string _OptionText(SourceConfig config) =>
        string.Join(",", config.OptionTexts.Select(static pair => pair.Key + "=" + pair.Value));

    private static PluginError _CreateFailure(SourceConfig config)
    {
        using SettingsManager session = new();
        if (config.TryCreateSource(session.ReadOnly, out IFrameSource? source, out PluginError? error))
        {
            source.Dispose();
            throw new InvalidOperationException("The source was created.");
        }

        return error.Value;
    }

    #endregion

    #region Parse

    [Test]
    [Arguments("capture.pcapng", "PCAPNG")]
    [Arguments("x.PCAP", "PCAP")]
    [Arguments("data.blf", "BLF")]
    [Arguments("log.asc", "ASC")]
    public async Task BarePathsFindTheirKindByExtension(string spec, string kind)
    {
        using BuiltInRegistry registry = new();

        SourceConfig config = SourceConfig.Parse(spec, registry.Registry);

        await Assert.That(config.Registration.Kind).IsEqualTo(kind);
        await Assert.That(config.Path).IsEqualTo(spec);
        await Assert.That(config.OptionTexts).IsEmpty();
    }

    [Test]
    [Arguments("pcap:path=f.pcap", "PCAP", "f.pcap")]
    [Arguments("pcapng:path=f.pcapng", "PCAPNG", "f.pcapng")]
    [Arguments("PCAPNG:path=f.pcapng", "PCAPNG", "f.pcapng")]
    [Arguments("blf:path=x.blf", "BLF", "x.blf")]
    [Arguments("asc:path=x.asc", "ASC", "x.asc")]
    public async Task TypedSpecsFindTheirKindByName(string spec, string kind, string path)
    {
        using BuiltInRegistry registry = new();

        SourceConfig config = SourceConfig.Parse(spec, registry.Registry);

        await Assert.That(config.Registration.Kind).IsEqualTo(kind);
        await Assert.That(config.Path).IsEqualTo(path);
        await Assert.That(config.OptionTexts).IsEmpty();
    }

    [Test]
    public async Task ARandomSpecKeepsItsOptionTextsInOrder()
    {
        using BuiltInRegistry registry = new();

        SourceConfig config = SourceConfig.Parse("random:count=10,seed=7,mode=udp4", registry.Registry);

        await Assert.That(config.Registration.Kind).IsEqualTo("Random");
        await Assert.That(config.Path).IsNull();
        await Assert.That(_OptionText(config)).IsEqualTo("count=10,seed=7,mode=udp4");
    }

    [Test]
    [Arguments("random", "fullrandom")]
    [Arguments("full", "fullrandom")]
    [Arguments("eth", "ethernet")]
    [Arguments("ip4", "ipv4")]
    [Arguments("ip6", "ipv6")]
    [Arguments("udpipv4", "udp4")]
    [Arguments("udpipv6", "udp6")]
    [Arguments("can20", "can")]
    [Arguments("fd", "canfd")]
    [Arguments("udp4", "udp4")]
    public async Task LegacyRandomModesMapToTheNamesOfTheOption(string legacy, string expected)
    {
        using BuiltInRegistry registry = new();

        SourceConfig config = SourceConfig.Parse("random:mode=" + legacy, registry.Registry);

        await Assert.That(_OptionText(config)).IsEqualTo("mode=" + expected);
    }

    [Test]
    public async Task OptionProblemsAppearWhenTheSourceIsCreated()
    {
        using BuiltInRegistry registry = new();

        PluginError count = _CreateFailure(SourceConfig.Parse("random:count=-1", registry.Registry));
        PluginError mode = _CreateFailure(SourceConfig.Parse("random:mode=notamode", registry.Registry));
        PluginError seed = _CreateFailure(SourceConfig.Parse("random:seed=notanumber", registry.Registry));
        PluginError twice = _CreateFailure(SourceConfig.Parse("random:count=1,count=2", registry.Registry));

        await Assert.That(count.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(count.Message).IsEqualTo(Invariant($"count: allowed 0 – {Array.MaxLength}"));
        await Assert.That(mode.Message).StartsWith("mode: allowed: fullrandom, ethernet, ipv4");
        await Assert.That(seed.Message).StartsWith("seed: 'notanumber' is not a whole number");
        await Assert.That(twice.Message).IsEqualTo("Option 'count' is given twice.");
    }

    [Test]
    public async Task WithDefaultOptionAddsAnOptionOnlyWhenTheSpecDoesNotSetIt()
    {
        using BuiltInRegistry registry = new();
        SourceConfig plain = SourceConfig.Parse("data.blf", registry.Registry);
        SourceConfig own = SourceConfig.Parse("blf:path=data.blf,cacheBudgetBytes=1000", registry.Registry);

        SourceConfig added = plain.WithDefaultOption("cacheBudgetBytes", "2048");
        SourceConfig kept = own.WithDefaultOption("cacheBudgetBytes", "2048");

        await Assert.That(_OptionText(added)).IsEqualTo("cacheBudgetBytes=2048");
        await Assert.That(_OptionText(plain)).IsEmpty();
        await Assert.That(_OptionText(kept)).IsEqualTo("cacheBudgetBytes=1000");
        await Assert.That(ReferenceEquals(kept, own)).IsTrue();
    }

    [Test]
    public async Task OptionsAsJsonUsesJsonStringsAndTheLastTextOfAKeyWins()
    {
        using BuiltInRegistry registry = new();
        SourceConfig config = SourceConfig.Parse("random:count=1,seed=5,count=9", registry.Registry);

        IReadOnlyDictionary<string, JsonElement> options = config.OptionsAsJson();

        await Assert.That(options.Count).IsEqualTo(2);
        await Assert.That(options["count"].GetString()).IsEqualTo("9");
        await Assert.That(options["seed"].GetString()).IsEqualTo("5");
    }

    #endregion

    #region Errors

    [Test]
    [Arguments("file.xyz")]
    [Arguments("foo:path=bar")]
    [Arguments("a.pcapng|whoami")]
    [Arguments("a.pcapng;rm")]
    [Arguments("pcap:path=")]
    [Arguments("blf:")]
    [Arguments("asc:foo=bar")]
    public async Task BadSpecsFailWithAnArgumentException(string spec)
    {
        using BuiltInRegistry registry = new();

        await Assert.That(() => SourceConfig.Parse(spec, registry.Registry)).Throws<ArgumentException>();
    }

    [Test]
    public async Task AnUnknownKindListsTheKnownKinds()
    {
        using BuiltInRegistry registry = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => SourceConfig.Parse("foo:path=bar", registry.Registry));

        await Assert.That(exception.Message).IsEqualTo("Unknown source kind 'foo'. Known kinds: BLF, PCAPNG, PCAP, ASC, Random.");
    }

    [Test]
    public async Task AFileKindWithoutAPathNamesTheParameter()
    {
        using BuiltInRegistry registry = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => SourceConfig.Parse("blf:", registry.Registry));

        await Assert.That(exception.Message).IsEqualTo("BLF source requires 'path' parameter (e.g., blf:path=file.blf).");
    }

    [Test]
    public async Task AWindowsDrivePathIsABarePath()
    {
        using BuiltInRegistry registry = new();

        SourceConfig config = SourceConfig.Parse(@"C:\data\trace.blf", registry.Registry);

        await Assert.That(config.Registration.Kind).IsEqualTo("BLF");
        await Assert.That(config.Path).IsEqualTo(@"C:\data\trace.blf");
    }

    [Test]
    public async Task AMissingFileIsAFileNotFoundErrorOfTheRegistration()
    {
        using BuiltInRegistry registry = new();
        SourceConfig config = SourceConfig.Parse("definitely-missing-file-xyz.pcapng", registry.Registry);

        PluginError error = _CreateFailure(config);

        await Assert.That(error.Code).IsEqualTo(PluginErrorCode.FileNotFound);
    }

    #endregion

    #region Create

    [Test]
    public async Task ARandomSpecCreatesTheCoreSourceWithTheGivenOptions()
    {
        using BuiltInRegistry registry = new();
        SourceConfig config = SourceConfig.Parse("random:count=3,seed=7,mode=udp4", registry.Registry);
        using SettingsManager session = new();

        bool created = config.TryCreateSource(session.ReadOnly, out IFrameSource? source, out PluginError? error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsNull();
        IFrameSource opened = Expect.NotNull(source);
        using (opened)
        {
            await Assert.That(opened).IsTypeOf<RandomFrameSource>();
            await Assert.That(opened.EstimatedFrameCount).IsEqualTo(3);
        }
    }

    #endregion
}
