// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The five built-in source registrations: formats, options, errors, metadata, and the registry order with a plugin.</summary>
internal sealed class BuiltInSourceTests
{
    #region Helpers

    // The four file kinds with the file each one reads in a trace; the legacy file goes with the PCAP kind.
    private static (string Name, SourceRegistration Registration, string Path)[] _FileKinds(SmallTraceWriter.Trace trace) =>
    [
        ("BLF", BlfSourceRegistration.Create(), trace.Blf),
        ("PCAPNG", PcapngSourceRegistration.Create(), trace.Pcapng),
        ("PCAP", PcapSourceRegistration.Create(), trace.Pcap),
        ("ASC", AscSourceRegistration.Create(), trace.Asc),
    ];

    private static IFrameSource _Open(SourceRegistration registration, string[] paths, params (string Name, SettingValue Value)[] options)
    {
        (IFrameSource? source, PluginError? error) = RegistrationRunner.Open(registration, paths, options);
        if (source is null)
        {
            throw new InvalidOperationException(Expect.NotNull(error).Message);
        }

        return source;
    }

    private static PluginError _Fail(SourceRegistration registration, string[] paths, params (string Name, SettingValue Value)[] options)
    {
        (IFrameSource? source, PluginError? error) = RegistrationRunner.Open(registration, paths, options);
        source?.Dispose();
        return Expect.NotNull(error);
    }

    #endregion

    #region Formats

    [Test]
    public async Task EveryRegistrationOpensItsFileAndYieldsTheFramesAsCoreSources()
    {
        using TempFolder folder = new("src-formats");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 50, seed: 1);
        Type[] expectedTypes = [typeof(BlfSource), typeof(PcapngSource), typeof(PcapngSource), typeof(AscSource)];

        (string Name, SourceRegistration Registration, string Path)[] kinds = _FileKinds(trace);
        for (int i = 0; i < kinds.Length; i++)
        {
            using IFrameSource source = _Open(kinds[i].Registration, [kinds[i].Path]);
            List<Frame> frames = SmallTraceWriter.ReadAll(source);

            // The Core source itself comes back: no wrapper type stands between the caller and the reader.
            await Assert.That(source.GetType()).IsEqualTo(expectedTypes[i]);
            await Assert.That($"{kinds[i].Name}:{frames.Count}").IsEqualTo($"{kinds[i].Name}:50");
        }

        // The Random registration with the options of the trace yields the bytes that the PCAPNG file holds.
        using IFrameSource random = _Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(50)), ("seed", SettingValue.U64(1)), ("mode", SettingValue.Enum("udp4", 4)));
        using IFrameSource pcapng = _Open(PcapngSourceRegistration.Create(), [trace.Pcapng]);
        List<Frame> randomFrames = SmallTraceWriter.ReadAll(random);
        List<Frame> fileFrames = SmallTraceWriter.ReadAll(pcapng);

        await Assert.That(random).IsTypeOf<RandomFrameSource>();
        await Assert.That(randomFrames.Count).IsEqualTo(50);
        for (int i = 0; i < 50; i++)
        {
            await Assert.That(randomFrames[i].Data.Span.SequenceEqual(fileFrames[i].Data.Span)).IsTrue();
        }
    }

    #endregion

    #region Options

    [Test]
    public async Task BlfOptionsReachTheCoreSourceOptions()
    {
        using TempFolder folder = new("src-blf-options");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 5, seed: 2);

        using IFrameSource source = _Open(BlfSourceRegistration.Create(), [trace.Blf],
            ("scanMode", SettingValue.Enum("full", 1)), ("errorTolerance", SettingValue.Enum("strict", 1)),
            ("cacheBudgetBytes", SettingValue.I64(1_048_576)), ("uiName", SettingValue.String("Run 1")));

        BlfSource blf = (BlfSource)source;
        await Assert.That(blf.UiName).IsEqualTo("Run 1");
        await Assert.That(blf.ErrorTolerance).IsEqualTo(ErrorToleranceMode.Strict);
    }

    [Test]
    public async Task AnEmptyUiNameKeepsTheFileNameAsTheSourceName()
    {
        using TempFolder folder = new("src-ui-name");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 5, seed: 2);

        using IFrameSource source = _Open(BlfSourceRegistration.Create(), [trace.Blf]);

        await Assert.That(source.UiName).IsEqualTo("trace.blf");
    }

    [Test]
    public async Task AZeroPreloadBudgetStillReadsAllFramesThroughTheMappedPath()
    {
        using TempFolder folder = new("src-preload");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 50, seed: 3);

        using IFrameSource source = _Open(PcapngSourceRegistration.Create(), [trace.Pcapng], ("preloadBudgetBytes", SettingValue.I64(0)));

        await Assert.That(SmallTraceWriter.ReadAll(source).Count).IsEqualTo(50);
    }

    [Test]
    public async Task AscReadsWithAnExplicitTimeZoneAndSlabs()
    {
        using TempFolder folder = new("src-asc-options");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 20, seed: 3);

        using IFrameSource source = _Open(AscSourceRegistration.Create(), [trace.Asc],
            ("timestampTimeZone", SettingValue.String("UTC")), ("frameSlabBytes", SettingValue.I64(65536)));

        await Assert.That(SmallTraceWriter.ReadAll(source).Count).IsEqualTo(20);
    }

    [Test]
    public async Task RandomRateModeAndSizesReachTheGenerator()
    {
        using IFrameSource thousand = _Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(3)), ("framesPerSecond", SettingValue.F64(1000)));
        using IFrameSource quarter = _Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(3)), ("framesPerSecond", SettingValue.F64(250)));
        using IFrameSource can = _Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(3)), ("mode", SettingValue.Enum("can", 6)));
        using IFrameSource sized = _Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(20)), ("mode", SettingValue.Enum("ethernet", 1)),
            ("minFrameSize", SettingValue.I64(100)), ("maxFrameSize", SettingValue.I64(120)));

        List<Frame> thousandFrames = SmallTraceWriter.ReadAll(thousand);
        List<Frame> quarterFrames = SmallTraceWriter.ReadAll(quarter);
        List<Frame> canFrames = SmallTraceWriter.ReadAll(can);
        List<Frame> sizedFrames = SmallTraceWriter.ReadAll(sized);

        await Assert.That(thousandFrames[1].Timestamp.AsNanos - thousandFrames[0].Timestamp.AsNanos).IsEqualTo(1_000_000L);
        await Assert.That(quarterFrames[1].Timestamp.AsNanos - quarterFrames[0].Timestamp.AsNanos).IsEqualTo(4_000_000L);
        await Assert.That(canFrames[0].LinkType).IsEqualTo(LinkType.CanSocketcan);
        await Assert.That(sizedFrames.All(static frame => frame.Length is >= 100 and <= 120)).IsTrue();
    }

    [Test]
    public async Task RandomDefaultsEqualTheCommandLineDefaults()
    {
        // 1000 frames, seed 42, UDP over IPv4 and a 1 ms interval are what "random:" gives on the command line today.
        using IFrameSource source = _Open(RandomSourceRegistration.Create(), []);
        using IFrameSource reference = new RandomFrameSource(1000, 42, RandomFrameMode.UdpIPv4);

        List<Frame> frames = SmallTraceWriter.ReadAll(source);
        List<Frame> expected = SmallTraceWriter.ReadAll(reference);

        await Assert.That(frames.Count).IsEqualTo(1000);
        await Assert.That(frames[999].Data.Span.SequenceEqual(expected[999].Data.Span)).IsTrue();
        await Assert.That(frames[1].Timestamp.AsNanos - frames[0].Timestamp.AsNanos)
            .IsEqualTo(expected[1].Timestamp.AsNanos - expected[0].Timestamp.AsNanos);
    }

    #endregion

    #region Errors

    [Test]
    public async Task APathProblemIsAnOptionErrorOnPaths()
    {
        using TempFolder folder = new("src-path-errors");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 5, seed: 4);
        string missing = folder.FilePath("missing.dat");

        foreach ((string name, SourceRegistration registration, string path) in _FileKinds(trace))
        {
            PluginError none = _Fail(registration, []);
            PluginError two = _Fail(registration, [path, path]);
            PluginError absent = _Fail(registration, [missing]);
            PluginError directory = _Fail(registration, [folder.Path]);
            PluginError blank = _Fail(registration, [" "]);

            await Assert.That($"{name}:{none.Code}:{none.Option}:{none.Message}")
                .IsEqualTo($"{name}:InvalidOption:paths:A file path is required.");
            await Assert.That($"{name}:{two.Code}:{two.Option}:{two.Message}")
                .IsEqualTo($"{name}:InvalidOption:paths:Exactly one file per source; open several sources for several files.");
            await Assert.That($"{name}:{absent.Code}:{absent.Message}").IsEqualTo($"{name}:FileNotFound:File '{missing}' does not exist.");
            await Assert.That($"{name}:{directory.Code}").IsEqualTo($"{name}:FileNotFound");
            await Assert.That($"{name}:{blank.Code}:{blank.Option}:{blank.Message}")
                .IsEqualTo($"{name}:InvalidOption:paths:The file path is empty.");
        }
    }

    [Test]
    public async Task AFileOfAnotherFormatIsUnsupported()
    {
        using TempFolder folder = new("src-garbage");
        byte[] noise = new byte[4096];
        new System.Random(7).NextBytes(noise);
        string blf = folder.FilePath("garbage.blf");
        string pcapng = folder.FilePath("garbage.pcapng");
        string pcap = folder.FilePath("garbage.pcap");
        File.WriteAllBytes(blf, noise);
        File.WriteAllBytes(pcapng, noise);
        File.WriteAllBytes(pcap, noise);

        PluginError fromBlf = _Fail(BlfSourceRegistration.Create(), [blf]);
        PluginError fromPcapng = _Fail(PcapngSourceRegistration.Create(), [pcapng]);
        PluginError fromPcap = _Fail(PcapSourceRegistration.Create(), [pcap]);

        await Assert.That(fromBlf.Code).IsEqualTo(PluginErrorCode.UnsupportedFormat);
        await Assert.That(fromBlf.Message).StartsWith($"'{blf}' is not a valid BLF file: ");
        await Assert.That(fromPcapng.Code).IsEqualTo(PluginErrorCode.UnsupportedFormat);
        await Assert.That(fromPcapng.Message).StartsWith($"'{pcapng}' is not a valid PCAPNG file: ");
        await Assert.That(fromPcap.Code).IsEqualTo(PluginErrorCode.UnsupportedFormat);
        await Assert.That(fromPcap.Message).StartsWith($"'{pcap}' is not a valid PCAP file: ");
    }

    [Test]
    public async Task OptionValuesThatCoreSettingsCannotJudgeAreOptionErrors()
    {
        using TempFolder folder = new("src-option-errors");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 5, seed: 5);

        foreach ((string name, SourceRegistration registration, string path) in _FileKinds(trace))
        {
            PluginError slab = _Fail(registration, [path], ("frameSlabBytes", SettingValue.I64(100)));
            await Assert.That($"{name}:{slab.Code}:{slab.Option}").IsEqualTo($"{name}:InvalidOption:frameSlabBytes");
        }

        PluginError zoneBlf = _Fail(BlfSourceRegistration.Create(), [trace.Blf], ("timestampTimeZone", SettingValue.String("Mars/Base")));
        PluginError zoneAsc = _Fail(AscSourceRegistration.Create(), [trace.Asc], ("timestampTimeZone", SettingValue.String("Mars/Base")));
        PluginError sizes = _Fail(RandomSourceRegistration.Create(), [],
            ("minFrameSize", SettingValue.I64(100)), ("maxFrameSize", SettingValue.I64(50)));
        PluginError withPath = _Fail(RandomSourceRegistration.Create(), [trace.Pcapng]);

        await Assert.That(zoneBlf.Option).IsEqualTo("timestampTimeZone");
        await Assert.That(zoneBlf.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(zoneAsc.Option).IsEqualTo("timestampTimeZone");
        await Assert.That(sizes.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(sizes.Option).IsEqualTo("minFrameSize");
        await Assert.That(withPath.Option).IsEqualTo("paths");
        await Assert.That(withPath.Message).IsEqualTo("The Random source reads no files.");
    }

    [Test]
    public async Task AnEmptyOrTruncatedFileIsUnsupportedNotUnreadable()
    {
        using TempFolder folder = new("src-truncated");
        SmallTraceWriter.Trace trace = SmallTraceWriter.Write(folder, count: 20, seed: 6);
        string emptyBlf = folder.FilePath("empty.blf");
        string emptyPcapng = folder.FilePath("empty.pcapng");
        string emptyPcap = folder.FilePath("empty.pcap");
        File.WriteAllBytes(emptyBlf, []);
        File.WriteAllBytes(emptyPcapng, []);
        File.WriteAllBytes(emptyPcap, []);
        string shortBlf = folder.FilePath("short.blf");
        string shortPcapng = folder.FilePath("short.pcapng");
        File.WriteAllBytes(shortBlf, File.ReadAllBytes(trace.Blf)[..10]);
        File.WriteAllBytes(shortPcapng, File.ReadAllBytes(trace.Pcapng)[..10]);

        PluginError[] errors =
        [
            _Fail(BlfSourceRegistration.Create(), [emptyBlf]),
            _Fail(PcapngSourceRegistration.Create(), [emptyPcapng]),
            _Fail(PcapSourceRegistration.Create(), [emptyPcap]),
            _Fail(BlfSourceRegistration.Create(), [shortBlf]),
            _Fail(PcapngSourceRegistration.Create(), [shortPcapng]),
        ];

        foreach (PluginError error in errors)
        {
            await Assert.That(error.Code).IsEqualTo(PluginErrorCode.UnsupportedFormat);
        }
    }

    [Test]
    public async Task ExtremeRandomNumbersNeverThrow()
    {
        // The largest count the Core setting allows must still create a source (nothing is allocated up front).
        (IFrameSource? largest, PluginError? largestError) = RegistrationRunner.Open(RandomSourceRegistration.Create(), [],
            ("count", SettingValue.I64(Array.MaxLength)));
        largest?.Dispose();
        await Assert.That(largestError).IsNull();
        await Assert.That(largest).IsNotNull();

        // A rate that is not a number must be refused by the setting or by Create, never turn into an exception.
        bool refused = false;
        try
        {
            (IFrameSource? source, PluginError? error) = RegistrationRunner.Open(RandomSourceRegistration.Create(), [],
                ("framesPerSecond", SettingValue.F64(double.NaN)));
            source?.Dispose();
            refused = source is null && error is not null;
        }
        catch (SettingsException)
        {
            refused = true;
        }

        await Assert.That(refused).IsTrue();
    }

    [Test]
    public async Task AFailedOpenCreatesNoSourceAndNeverThrows()
    {
        (IFrameSource? source, PluginError? error) = RegistrationRunner.Open(BlfSourceRegistration.Create(), []);

        await Assert.That(source).IsNull();
        await Assert.That(error).IsNotNull();
    }

    #endregion

    #region Metadata

    [Test]
    public async Task KindsExtensionsAndOptionsAreDescribed()
    {
        SourceRegistration[] registrations =
        [
            BlfSourceRegistration.Create(), PcapngSourceRegistration.Create(), PcapSourceRegistration.Create(),
            AscSourceRegistration.Create(), RandomSourceRegistration.Create(),
        ];
        string[] extensions = [".blf", ".pcapng", ".pcap", ".asc", ""];

        await Assert.That(registrations.Select(static r => r.Kind).ToArray())
            .IsEquivalentTo(["BLF", "PCAPNG", "PCAP", "ASC", "Random"], CollectionOrdering.Matching);
        for (int i = 0; i < registrations.Length; i++)
        {
            SourceRegistration registration = registrations[i];
            await Assert.That(string.Join(",", registration.FileExtensions)).IsEqualTo(extensions[i]);
            await Assert.That(registration.AllowsMultipleFiles).IsEqualTo(registration.Kind != "Random");
            await Assert.That(registration.UiName).IsNotEmpty();
            await Assert.That(registration.Description).IsNotEmpty();

            // Every option is a documented Core setting in the default group.
            using SettingsManager options = RegistrationOptions.CreateDefaults(registration.RegisterOptions);
            await Assert.That(options.AllSettings.Count).IsGreaterThan(0);
            foreach (Setting setting in options.AllSettings)
            {
                string described = $"{setting.UiName.Length > 0}:{setting.Description?.Length > 0}:{setting.GroupName.Length}";
                await Assert.That($"{registration.Kind}.{setting.Name}:{described}")
                    .IsEqualTo($"{registration.Kind}.{setting.Name}:True:True:0");
            }
        }
    }

    [Test]
    public async Task OptionListsMatchTheCoreSourceOptions()
    {
        using SettingsManager blf = RegistrationOptions.CreateDefaults(BlfSourceRegistration.Create().RegisterOptions);
        using SettingsManager pcapng = RegistrationOptions.CreateDefaults(PcapngSourceRegistration.Create().RegisterOptions);
        using SettingsManager asc = RegistrationOptions.CreateDefaults(AscSourceRegistration.Create().RegisterOptions);

        await Assert.That(blf.AllSettings.Select(static s => s.Name).Order().ToArray()).IsEquivalentTo(
            ["cacheBudgetBytes", "errorTolerance", "frameSlabBytes", "maxContainerBytes", "maxDecompressionConcurrency",
                "preloadBudgetBytes", "scanMode", "timestampTimeZone", "uiName"], CollectionOrdering.Matching);
        await Assert.That(pcapng.AllSettings.Select(static s => s.Name).Order().ToArray()).IsEquivalentTo(
            ["errorTolerance", "frameSlabBytes", "preloadBudgetBytes", "scanMode", "uiName"], CollectionOrdering.Matching);

        // ASC always scans completely, so it offers no scan mode.
        await Assert.That(asc.AllSettings.Select(static s => s.Name).Order().ToArray()).IsEquivalentTo(
            ["errorTolerance", "frameSlabBytes", "preloadBudgetBytes", "timestampTimeZone", "uiName"], CollectionOrdering.Matching);
        await Assert.That(blf.GetI64Setting("preloadBudgetBytes")).IsEqualTo(new BlfSourceOptions().PreloadBudget);
        await Assert.That(asc.GetI64Setting("preloadBudgetBytes")).IsEqualTo(AscSourceOptions.DefaultPreloadBudget);
    }

    #endregion

    #region Registry

    [Test]
    public async Task ASamplePluginSourceSitsBehindTheBuiltInsAndRunsThroughTheSameLoop()
    {
        using LoaderHarness harness = new("src-registry");
        string pluginFolder = Path.Combine(harness.Folder.Path, "plugins");
        TestPluginFiles.CopyInto(TestPluginFiles.Sample, pluginFolder);
        harness.Load([pluginFolder], NetworkInspectorBuiltIns.CreateAll());
        PluginRegistry registry = harness.Registry;

        await Assert.That(registry.Sources.Select(static source => source.Kind).ToArray())
            .IsEquivalentTo(["BLF", "PCAPNG", "PCAP", "ASC", "Random", "SampleGen"], CollectionOrdering.Matching);

        // The plugin source is created and read exactly like a built-in one, and a standard stack with the sample
        // contribution decodes its frames.
        await Assert.That(registry.TryGetSource("SampleGen", out SourceRegistration? sample, out _)).IsTrue();
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        IReadOnlyList<PluginContributionFailure> failures = registry.ApplyStackContributions(builder);
        Stack stack = builder.Build();
        (IFrameSource? source, PluginError? error) = RegistrationRunner.Open(Expect.NotNull(sample), [],
            ("count", SettingValue.I64(3)));
        using IFrameSource created = Expect.NotNull(source);
        List<Frame> frames = SmallTraceWriter.ReadAll(created, stack.FrameInterfaceRegistry);

        await Assert.That(error).IsNull();
        await Assert.That(failures).IsEmpty();
        await Assert.That(frames.Count).IsEqualTo(3);
        Packet packet = SampleStack.Parse(stack, frames[0]);
        await Assert.That(SampleStack.U64(stack, packet, "sample.counter")).IsEqualTo(0UL);
    }

    #endregion
}
