// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>The nine built-in exporter registrations: formats, round trips, options, errors, and the Core listeners they return.</summary>
internal sealed class BuiltInExporterTests
{
    #region Helpers

    // The registrations exactly as the exporters plugin declares them.
    private static (List<ExporterRegistration<IFrameListener>> Frames, List<ExporterRegistration<IPacketListener>> Packets) _Registrations()
    {
        RecordingPluginBuilder recording = new();
        new ExportersPlugin().Configure(recording);
        return (recording.FrameExporters, recording.PacketExporters);
    }

    private static ExporterRegistration<IPacketListener> _Packet(string format) =>
        _Registrations().Packets.Single(r => r.Format == format);

    private static ExporterRegistration<IFrameListener> _Frame(string format) =>
        _Registrations().Frames.Single(r => r.Format == format);

    // A standard stack with parsed Random UDP packets; the frames belong to the stack's interface registry.
    private static (Stack Stack, List<Frame> Frames, List<Packet> Packets) _Traffic(int count, ulong seed)
    {
        Stack stack = SampleStack.Build(withSample: false);
        List<Frame> frames = SmallTraceWriter.Generate(count, seed, stack.FrameInterfaceRegistry);
        List<Packet> packets = [.. frames.Select(frame => SampleStack.Parse(stack, frame))];
        return (stack, frames, packets);
    }

    private static void _Dispose(object? listener) => (listener as IDisposable)?.Dispose();

    private static T _Ok<T>((T? Listener, PluginError? Error) result)
        where T : class
    {
        if (result.Listener is null)
        {
            throw new InvalidOperationException(Expect.NotNull(result.Error).Message);
        }

        return result.Listener;
    }

    private static void _Write(IPacketListener listener, List<Packet> packets)
    {
        foreach (Packet packet in packets)
        {
            listener.OnPacket(packet);
        }

        listener.OnFinish();
    }

    private static void _Write(IFrameListener listener, List<Frame> frames)
    {
        foreach (Frame frame in frames)
        {
            listener.OnFrame(frame);
        }

        listener.OnFinish();
    }

    // Writes the packets through a registration with the given options and returns the target path.
    private static string _WritePackets(TempFolder folder, string format, string name, (Stack Stack, List<Frame> Frames, List<Packet> Packets) traffic,
        params (string Name, SettingValue Value)[] options)
    {
        string target = folder.FilePath(name);
        IPacketListener listener = _Ok(RegistrationRunner.Create(_Packet(format), target, traffic.Stack, options));
        _Write(listener, traffic.Packets);
        _Dispose(listener);
        return target;
    }

    #endregion

    #region Formats

    [Test]
    public async Task AllNineRegistrationsWriteTheSameTwentyPackets()
    {
        using TempFolder folder = new("exp-formats");
        (Stack stack, List<Frame> frames, List<Packet> packets) = _Traffic(20, 2);
        (List<ExporterRegistration<IFrameListener>> frameRegistrations, List<ExporterRegistration<IPacketListener>> packetRegistrations) = _Registrations();

        await Assert.That(frameRegistrations.Select(static r => $"{r.Format}{r.FileExtension}").ToArray())
            .IsEquivalentTo(["BLF.blf", "PCAPNG.pcapng", "ASC.asc"], CollectionOrdering.Matching);
        await Assert.That(packetRegistrations.Select(static r => $"{r.Format}{r.FileExtension}").ToArray())
            .IsEquivalentTo(["CSV.csv", "JSON.json", "Text.txt", "PBF.pbf", "Parquet", "DuckDB.duckdb"], CollectionOrdering.Matching);

        // Frame formats receive the frames, packet formats the parsed packets; each finishes after 20 items.
        foreach (ExporterRegistration<IFrameListener> registration in frameRegistrations)
        {
            string target = folder.FilePath("out" + registration.FileExtension);
            IFrameListener listener = _Ok(RegistrationRunner.Create(registration, target, stack));
            _Write(listener, frames);
            IExporterStatistics statistics = (IExporterStatistics)listener;
            await Assert.That($"{registration.Format}:{statistics.WrittenCount}:{statistics.IsFinished}").IsEqualTo($"{registration.Format}:20:True");
            _Dispose(listener);
            await Assert.That(new FileInfo(target).Length).IsGreaterThan(0L);
        }

        foreach (ExporterRegistration<IPacketListener> registration in packetRegistrations)
        {
            string target = folder.FilePath("out" + registration.FileExtension);
            IPacketListener listener = _Ok(RegistrationRunner.Create(registration, target, stack));
            _Write(listener, packets);
            IExporterStatistics statistics = (IExporterStatistics)listener;
            await Assert.That($"{registration.Format}:{statistics.WrittenCount}:{statistics.IsFinished}").IsEqualTo($"{registration.Format}:20:True");
            _Dispose(listener);

            // Parquet writes a dataset folder; every other format a single non-empty file.
            bool written = registration.FileExtension.Length == 0
                ? Directory.EnumerateFileSystemEntries(target).Any()
                : new FileInfo(target).Length > 0;
            await Assert.That($"{registration.Format}:{written}").IsEqualTo($"{registration.Format}:True");
        }
    }

    [Test]
    public async Task BlfPcapngAndAscFilesReadBackThroughTheMatchingSources()
    {
        using TempFolder folder = new("exp-round-trip");
        (Stack stack, List<Frame> frames, _) = _Traffic(100, 3);

        foreach ((string format, string extension, SourceRegistration source) in new (string, string, SourceRegistration)[]
        {
            ("BLF", ".blf", BlfSourceRegistration.Create()),
            ("PCAPNG", ".pcapng", PcapngSourceRegistration.Create()),
            ("ASC", ".asc", AscSourceRegistration.Create()),
        })
        {
            string target = folder.FilePath("round" + extension);
            IFrameListener listener = _Ok(RegistrationRunner.Create(_Frame(format), target, stack));
            _Write(listener, frames);
            _Dispose(listener);

            (IFrameSource? opened, PluginError? error) = RegistrationRunner.Open(source, [target]);
            using IFrameSource reopened = Expect.NotNull(opened);
            List<Frame> read = SmallTraceWriter.ReadAll(reopened);

            await Assert.That(error).IsNull();
            await Assert.That($"{format}:{read.Count}").IsEqualTo($"{format}:100");
            for (int i = 0; i < 100; i++)
            {
                long difference = Math.Abs(read[i].Timestamp.AsNanos - frames[i].Timestamp.AsNanos);

                // BLF and PCAPNG keep nanoseconds; ASC writes six decimals and rounds to the nearest microsecond.
                long allowed = format == "ASC"
                    ? 500
                    : 0;
                await Assert.That(read[i].Data.Span.SequenceEqual(frames[i].Data.Span)).IsTrue();
                await Assert.That($"{format}:{i}:{difference <= allowed}").IsEqualTo($"{format}:{i}:True");
            }
        }
    }

    #endregion

    #region Options

    [Test]
    public async Task OptionsChangeTheOutputAndDefaultsEqualTheDirectBuilders()
    {
        using TempFolder folder = new("exp-options");
        (Stack stack, List<Frame> frames, List<Packet> packets) = _Traffic(20, 4);

        // Defaults must equal what the command line produced with the Core builders and no extra call.
        string csv = _WritePackets(folder, "CSV", "default.csv", (stack, frames, packets));
        using (CsvExporter direct = CsvExporter.CreateBuilder().ToFile(folder.FilePath("direct.csv")).Build())
        {
            _Write(direct, packets);
        }

        string json = _WritePackets(folder, "JSON", "default.json", (stack, frames, packets));
        using (JsonExporter direct = JsonExporter.CreateBuilder().ToFile(folder.FilePath("direct.json")).WithFormat(JsonExportFormat.Compact).Build())
        {
            _Write(direct, packets);
        }

        string text = _WritePackets(folder, "Text", "default.txt", (stack, frames, packets));
        using (TextExporter direct = TextExporter.CreateBuilder().ToFile(folder.FilePath("direct.txt"))
            .WithDetailLevel(TextDetailLevel.Standard).WithMaxTextLength(256).Build())
        {
            _Write(direct, packets);
        }

        string pbf = _WritePackets(folder, "PBF", "default.pbf", (stack, frames, packets));
        using (PbfExporter direct = PbfExporter.CreateBuilder().ToFile(folder.FilePath("direct.pbf"))
            .WithFormat(PbfExportFormat.Standard).WithCompressed(true).Build())
        {
            _Write(direct, packets);
        }

        await Assert.That(File.ReadAllBytes(csv).SequenceEqual(File.ReadAllBytes(folder.FilePath("direct.csv")))).IsTrue();
        await Assert.That(File.ReadAllBytes(json).SequenceEqual(File.ReadAllBytes(folder.FilePath("direct.json")))).IsTrue();
        await Assert.That(File.ReadAllBytes(text).SequenceEqual(File.ReadAllBytes(folder.FilePath("direct.txt")))).IsTrue();

        // The PBF header carries the creation time, so equal content shows as equal size, not as equal bytes.
        await Assert.That(new FileInfo(pbf).Length).IsEqualTo(new FileInfo(folder.FilePath("direct.pbf")).Length);
    }

    [Test]
    public async Task StyleLevelAndLayoutOptionsReachTheExporters()
    {
        using TempFolder folder = new("exp-effects");
        (Stack stack, List<Frame> frames, List<Packet> packets) = _Traffic(10, 5);
        (Stack, List<Frame>, List<Packet>) traffic = (stack, frames, packets);

        string array = _WritePackets(folder, "JSON", "array.json", traffic, ("style", SettingValue.Enum("array", 2)));
        string pretty = _WritePackets(folder, "JSON", "pretty.json", traffic, ("style", SettingValue.Enum("pretty", 1)));
        string compact = _WritePackets(folder, "JSON", "compact.json", traffic);
        string summary = _WritePackets(folder, "Text", "summary.txt", traffic, ("level", SettingValue.Enum("summary", 0)));
        string full = _WritePackets(folder, "Text", "full.txt", traffic, ("level", SettingValue.Enum("full", 2)));
        string columnar = _WritePackets(folder, "PBF", "columnar.pbf", traffic, ("format", SettingValue.Enum("columnar", 1)));
        string plain = _WritePackets(folder, "PBF", "plain.pbf", traffic, ("compressed", SettingValue.Bool(false)));
        string standard = _WritePackets(folder, "PBF", "standard.pbf", traffic);

        await Assert.That(File.ReadAllText(array).TrimStart()[0]).IsEqualTo('[');
        await Assert.That(File.ReadAllLines(pretty).Length).IsGreaterThan(File.ReadAllLines(compact).Length);
        await Assert.That(new FileInfo(summary).Length).IsLessThan(new FileInfo(full).Length);
        await Assert.That(File.ReadAllBytes(columnar).SequenceEqual(File.ReadAllBytes(standard))).IsFalse();
        await Assert.That(new FileInfo(plain).Length).IsGreaterThan(new FileInfo(standard).Length);
    }

    [Test]
    public async Task PcapngResolutionAndSnapLengthOptionsApply()
    {
        using TempFolder folder = new("exp-pcapng-options");
        (Stack stack, List<Frame> frames, _) = _Traffic(10, 6);
        string micro = folder.FilePath("micro.pcapng");
        string snap = folder.FilePath("snap.pcapng");

        IFrameListener microListener = _Ok(RegistrationRunner.Create(_Frame("PCAPNG"), micro, stack,
            ("timestampResolution", SettingValue.Enum("microseconds", 1)), ("comment", SettingValue.String("round trip"))));
        _Write(microListener, frames);
        _Dispose(microListener);
        IFrameListener snapListener = _Ok(RegistrationRunner.Create(_Frame("PCAPNG"), snap, stack, ("snapLength", SettingValue.U64(60))));
        _Write(snapListener, frames);
        _Dispose(snapListener);

        (IFrameSource? microSource, _) = RegistrationRunner.Open(PcapngSourceRegistration.Create(), [micro]);
        (IFrameSource? snapSource, _) = RegistrationRunner.Open(PcapngSourceRegistration.Create(), [snap]);
        using IFrameSource microOpened = Expect.NotNull(microSource);
        using IFrameSource snapOpened = Expect.NotNull(snapSource);
        List<Frame> microFrames = SmallTraceWriter.ReadAll(microOpened);
        List<Frame> snapFrames = SmallTraceWriter.ReadAll(snapOpened);

        // Microsecond resolution loses the nanoseconds; a snap length cuts the stored bytes.
        await Assert.That(microFrames.All(static frame => frame.Timestamp.AsNanos % 1000 == 0)).IsTrue();
        await Assert.That(snapFrames.All(static frame => frame.Length <= 60)).IsTrue();
        await Assert.That(snapFrames.Count).IsEqualTo(10);
    }

    [Test]
    public async Task AnOverlongPcapngCommentIsAnOptionError()
    {
        using TempFolder folder = new("exp-pcapng-comment");
        (Stack stack, _, _) = _Traffic(1, 7);

        (IFrameListener? listener, PluginError? error) = RegistrationRunner.Create(_Frame("PCAPNG"), folder.FilePath("c.pcapng"), stack,
            ("comment", SettingValue.String(new string('c', 65_536))));
        _Dispose(listener);

        await Assert.That(listener).IsNull();
        await Assert.That($"{error?.Code}:{error?.Option}").IsEqualTo("InvalidOption:comment");
    }

    [Test]
    public async Task CsvColumnsDelimiterHeaderAndBomFollowTheOptions()
    {
        using TempFolder folder = new("exp-csv");
        (Stack stack, List<Frame> frames, List<Packet> packets) = _Traffic(3, 7);
        (Stack, List<Frame>, List<Packet>) traffic = (stack, frames, packets);

        string custom = _WritePackets(folder, "CSV", "custom.csv", traffic, ("fields", SettingValue.String("ip.src;udp.dstport")), ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)),
            ("header", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)), ("delimiter", SettingValue.String(";")));
        string defaults = _WritePackets(folder, "CSV", "defaults.csv", traffic, ("bom", SettingValue.Bool(false)));
        string headerless = _WritePackets(folder, "CSV", "headerless.csv", traffic, ("header", SettingValue.Bool(false)),
            ("bom", SettingValue.Bool(false)));

        string[] lines = File.ReadAllLines(custom);
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(lines[0]).IsEqualTo("No.;Time;ip.src;udp.dstport");
        for (int i = 0; i < 3; i++)
        {
            string source = Expect.NotNull(SampleStack.Text(stack, packets[i], "ip.src"));
            ulong port = Expect.NotNull(SampleStack.U64(stack, packets[i], "udp.dstport"));
            await Assert.That(lines[i + 1]).IsEqualTo($"{packets[i].Id.Value};{packets[i].Timestamp.Format()};{source};{port}");
        }

        await Assert.That(File.ReadAllLines(defaults)[0]).IsEqualTo("No.,Time,Info,Length");
        await Assert.That(File.ReadAllLines(defaults).Length).IsEqualTo(4);
        await Assert.That(File.ReadAllLines(headerless).Length).IsEqualTo(3);
        await Assert.That(File.ReadAllBytes(defaults).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })).IsFalse();
    }

    [Test]
    public async Task CsvNeutralizesSpreadsheetFormulasByDefaultAndTheOptionTurnsItOff()
    {
        using TempFolder folder = new("exp-csv-formulas");
        Stack stack = SampleStack.Build(withSample: false);
        byte[] request = "GET =1+1 HTTP/1.1\r\nHost: example.test\r\n\r\n"u8.ToArray();
        Frame frame = SampleStack.MakeFrame(stack, RawFrames.Tcp(request, 50000, 80, sequence: 1));
        List<Packet> packets = [SampleStack.Parse(stack, frame)];
        (Stack, List<Frame>, List<Packet>) traffic = (stack, [frame], packets);

        // The request line of the packet carries text that the sender of the traffic chose: here a formula.
        string uri = Expect.NotNull(SampleStack.Text(stack, packets[0], "http.request.uri"));
        string safe = _WritePackets(folder, "CSV", "safe.csv", traffic, ("fields", SettingValue.String("http.request.uri")), ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)),
            ("bom", SettingValue.Bool(false)));
        string raw = _WritePackets(folder, "CSV", "raw.csv", traffic, ("fields", SettingValue.String("http.request.uri")), ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)),
            ("bom", SettingValue.Bool(false)), ("neutralizeFormulas", SettingValue.Bool(false)));

        await Assert.That(uri).IsEqualTo("=1+1");
        await Assert.That(File.ReadAllLines(safe)[1]).EndsWith(",'=1+1");
        await Assert.That(File.ReadAllLines(raw)[1]).EndsWith(",=1+1");
    }

    [Test]
    public async Task TheTimestampSortedOptionsDeclareAnOrderAndDoNotPromiseASort()
    {
        foreach (string format in new[] { "Parquet", "PBF" })
        {
            using SettingsManager options = RegistrationOptions.CreateDefaults(_Packet(format).RegisterOptions);
            Setting setting = Expect.NotNull(options.GetSetting("timestampSorted"));

            // The Core builder only records the declaration; a description that promises a sort (and memory use) would mislead.
            string description = Expect.NotNull(setting.Description);
            await Assert.That($"{format}:{setting.UiName}").IsEqualTo($"{format}:Packets arrive sorted");
            await Assert.That(description).Contains("Declares that the packets arrive in non-decreasing timestamp order");
            await Assert.That(description).Contains("sorts nothing");
            await Assert.That(description).DoesNotContain("more memory");
        }
    }

    [Test]
    public async Task TheCsvDelimiterDescriptionNamesTheRejectedCharactersAndAllowsATab()
    {
        using SettingsManager options = RegistrationOptions.CreateDefaults(_Packet("CSV").RegisterOptions);

        string description = Expect.NotNull(Expect.NotNull(options.GetSetting("delimiter")).Description);

        await Assert.That(description).Contains("NUL, CR, LF");
        await Assert.That(description).Contains("tab is allowed");
        await Assert.That(description).DoesNotContain("control character");
    }

    [Test]
    public async Task CsvAcceptsATabAsTheDelimiter()
    {
        using TempFolder folder = new("exp-csv-tab");
        (Stack stack, List<Frame> frames, List<Packet> packets) = _Traffic(2, 21);

        string tabbed = _WritePackets(folder, "CSV", "tab.csv", (stack, frames, packets), ("delimiter", SettingValue.String("\t")),
            ("bom", SettingValue.Bool(false)));

        // The option text says what is refused (NUL, CR, LF, and the double quote), and a tab is not one of them.
        await Assert.That(File.ReadAllLines(tabbed)[0]).IsEqualTo("No.\tTime\tInfo\tLength");
    }

    #endregion

    #region Errors

    [Test]
    public async Task CsvDelimiterAndColumnProblemsAreOptionErrors()
    {
        using TempFolder folder = new("exp-csv-errors");
        (Stack stack, _, _) = _Traffic(1, 8);

        foreach (string delimiter in new[] { "ab", "\"", "\n", "é", "", ":", "7", "A", "-" })
        {
            (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), folder.FilePath("d.csv"), stack,
                ("delimiter", SettingValue.String(delimiter)));
            _Dispose(listener);

            await Assert.That(listener).IsNull();
            await Assert.That($"{error?.Code}:{error?.Option}").IsEqualTo("InvalidOption:delimiter");
        }

        (IPacketListener? unknown, PluginError? columnError) = RegistrationRunner.Create(_Packet("CSV"), folder.FilePath("c.csv"), stack,
            ("fields", SettingValue.String("ip.src;nope.field")));
        _Dispose(unknown);

        await Assert.That(unknown).IsNull();
        await Assert.That(Expect.NotNull(columnError).Option).IsEqualTo("fields");
        await Assert.That(Expect.NotNull(columnError).Message).Contains("nope.field");
    }

    [Test]
    public async Task ABadTargetIsAnErrorAndSettingBoundsStopBadNumbersEarly()
    {
        using TempFolder folder = new("exp-target-errors");
        (Stack stack, _, _) = _Traffic(1, 9);
        string missingFolder = Path.Combine(folder.Path, "no-such-folder", "out");

        foreach (ExporterRegistration<IPacketListener> registration in _Registrations().Packets.Where(static r => r.Format is "JSON" or "CSV" or "PBF"))
        {
            (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(registration, missingFolder + registration.FileExtension, stack);
            _Dispose(listener);
            await Assert.That($"{registration.Format}:{error?.Code}").IsEqualTo($"{registration.Format}:FileNotFound");
        }

        (IFrameListener? blf, PluginError? blfError) = RegistrationRunner.Create(_Frame("BLF"), missingFolder + ".blf", stack);
        _Dispose(blf);
        await Assert.That(Expect.NotNull(blfError).Code).IsEqualTo(PluginErrorCode.FileNotFound);

        // The ASC decimals bound equals the builder range, so a wrong number never reaches Create.
        await Assert.That(() => RegistrationRunner.Create(_Frame("ASC"), folder.FilePath("a.asc"), stack,
            ("timestampDecimals", SettingValue.I64(5)))).Throws<ValidationSettingsException>();
        await Assert.That(() => RegistrationRunner.Create(_Frame("ASC"), folder.FilePath("a.asc"), stack,
            ("timestampDecimals", SettingValue.I64(10)))).Throws<ValidationSettingsException>();

        // A snap length of 0 means "exporter default" and succeeds.
        IFrameListener snap = _Ok(RegistrationRunner.Create(_Frame("PCAPNG"), folder.FilePath("s.pcapng"), stack, ("snapLength", SettingValue.U64(0))));
        _Dispose(snap);
        await Assert.That(snap).IsNotNull();
    }

    [Test]
    public async Task ADirectoryTargetWithATrailingSeparatorStillFindsItsParentFolder()
    {
        using TempFolder folder = new("exp-trailing");
        (Stack stack, _, _) = _Traffic(1, 12);
        string target = folder.FilePath("dataset") + Path.DirectorySeparatorChar;

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("Parquet"), target, stack);
        _Dispose(listener);

        await Assert.That(error).IsNull();
        await Assert.That(listener).IsNotNull();
    }

    [Test]
    public async Task ATimeZoneProblemOfTheBlfExporterIsAnOptionError()
    {
        using TempFolder folder = new("exp-zone");
        (Stack stack, _, _) = _Traffic(1, 10);

        (IFrameListener? listener, PluginError? error) = RegistrationRunner.Create(_Frame("BLF"), folder.FilePath("z.blf"), stack,
            ("timestampTimeZone", SettingValue.String("Mars/Base")));

        await Assert.That(listener).IsNull();
        await Assert.That(Expect.NotNull(error).Option).IsEqualTo("timestampTimeZone");
    }

    #endregion

    #region Listeners

    [Test]
    public async Task EveryListenerIsTheCoreExporterItself()
    {
        using TempFolder folder = new("exp-listeners");
        (Stack stack, _, _) = _Traffic(1, 11);
        (List<ExporterRegistration<IFrameListener>> frameRegistrations, List<ExporterRegistration<IPacketListener>> packetRegistrations) = _Registrations();
        Dictionary<string, Type> expected = new()
        {
            ["BLF"] = typeof(BlfExporter),
            ["PCAPNG"] = typeof(PcapngExporter),
            ["ASC"] = typeof(AscExporter),
            ["CSV"] = typeof(CsvExporter),
            ["JSON"] = typeof(JsonExporter),
            ["Text"] = typeof(TextExporter),
            ["PBF"] = typeof(PbfExporter),
            ["Parquet"] = typeof(ParquetExporter),
            ["DuckDB"] = typeof(DuckDbExporter),
        };
        List<(string Format, object Listener)> created = [];
        foreach (ExporterRegistration<IFrameListener> registration in frameRegistrations)
        {
            created.Add((registration.Format, _Ok(RegistrationRunner.Create(registration, folder.FilePath("f" + registration.FileExtension), stack))));
        }

        foreach (ExporterRegistration<IPacketListener> registration in packetRegistrations)
        {
            created.Add((registration.Format, _Ok(RegistrationRunner.Create(registration, folder.FilePath("p" + registration.FileExtension), stack))));
        }

        foreach ((string format, object listener) in created)
        {
            await Assert.That(listener.GetType()).IsEqualTo(expected[format]);
            await Assert.That(listener is IErrorTolerantExporter).IsTrue();
            await Assert.That(listener is IDisposable).IsTrue();
            string uiName = listener is IFrameListener frameListener
                ? frameListener.UiName
                : ((IPacketListener)listener).UiName;
            await Assert.That($"{format}:{uiName.Length > 0}").IsEqualTo($"{format}:True");
            _Dispose(listener);
        }

        // No wrapper sink exists: the assembly implements neither listener interface.
        Type[] wrappers = [.. typeof(NetworkInspectorBuiltIns).Assembly.GetTypes()
            .Where(static type => typeof(IFrameListener).IsAssignableFrom(type) || typeof(IPacketListener).IsAssignableFrom(type))];
        await Assert.That(wrappers).IsEmpty();
    }

    [Test]
    public async Task ExporterMetadataIsComplete()
    {
        (List<ExporterRegistration<IFrameListener>> frames, List<ExporterRegistration<IPacketListener>> packets) = _Registrations();

        foreach ((string format, string uiName, string description, Action<SettingsRegistrar> register) in frames
            .Select(static r => (r.Format, r.UiName, r.Description, r.RegisterOptions))
            .Concat(packets.Select(static r => (r.Format, r.UiName, r.Description, r.RegisterOptions))))
        {
            using SettingsManager options = RegistrationOptions.CreateDefaults(register);
            await Assert.That($"{format}:{uiName.Length > 0}:{description.Length > 0}").IsEqualTo($"{format}:True:True");
            foreach (Setting setting in options.AllSettings)
            {
                await Assert.That($"{format}.{setting.Name}:{setting.UiName.Length > 0}:{setting.Description?.Length > 0}:{setting.GroupName.Length}")
                    .IsEqualTo($"{format}.{setting.Name}:True:True:0");
            }
        }
    }

    #endregion
}
