// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// The content and batching options of the built-in exporter registrations: which format registers which option, that a value
/// reaches the exporter, and that a bad value is refused with a message that names the option or the field, before any file is
/// created.
/// </summary>
internal sealed class ExportContentOptionsTests
{
    #region Helpers

    private static (List<ExporterRegistration<IFrameListener>> Frames, List<ExporterRegistration<IPacketListener>> Packets) _Registrations()
    {
        RecordingPluginBuilder recording = new();
        new ExportersPlugin().Configure(recording);
        return (recording.FrameExporters, recording.PacketExporters);
    }

    private static ExporterRegistration<IPacketListener> _Packet(string format) =>
        _Registrations().Packets.Single(registration => registration.Format == format);

    private static string[] _OptionNames(Action<SettingsRegistrar> register)
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(register);
        return [.. manager.AllSettings.Select(static setting => setting.Name)];
    }

    private static Stack _Stack() => SampleStack.Build(withSample: false);

    private static string _Target(TempFolder folder, string format, string name) =>
        folder.FilePath(name + _Packet(format).FileExtension);

    private static void _Dispose(object? listener) => (listener as IDisposable)?.Dispose();

    #endregion

    #region Registration

    [Test]
    [Arguments("CSV", "shape,fields,occurrence,frames,buffers,text,repr,separator")]
    [Arguments("JSON", "shape,fields,occurrence,info,frames,buffers,text,repr")]
    [Arguments("Text", "shape,fields,occurrence,frames,buffers,text,repr")]
    [Arguments("PBF", "shape,fields,occurrence,info,frames,buffers,text,repr,writeMode,batchPackets,batchBytes")]
    [Arguments("Parquet", "shape,fields,occurrence,info,frames,buffers,text,repr,writeMode,batchPackets,batchBytes")]
    [Arguments("DuckDB", "shape,fields,occurrence,info,frames,buffers,text,repr,writeMode,batchPackets,batchBytes")]
    public async Task EveryFormatRegistersExactlyItsContentAndBatchingOptions(string format, string expectedCsv)
    {
        string[] names = _OptionNames(_Packet(format).RegisterOptions);

        foreach (string expected in expectedCsv.Split(','))
        {
            await Assert.That(names.Contains(expected)).IsTrue();
        }

        // Options a format cannot honor are not registered: CSV and Text have no info, and CSV, JSON, and Text have no batching.
        // Every packet format has the frames and the buffers option (CSV turns them into columns).
        if (format is "CSV" or "JSON" or "Text")
        {
            await Assert.That(names.Contains("batchPackets")).IsFalse();
        }

        if (format is "CSV" or "Text")
        {
            await Assert.That(names.Contains("info")).IsFalse();
        }

        // The former CSV column list is gone; the field list replaced it.
        await Assert.That(names.Contains("columns")).IsFalse();
    }

    [Test]
    public async Task BlfRegistersTheWriteModeAndTheContainerSizeWithTheirRange()
    {
        ExporterRegistration<IFrameListener> blf = _Registrations().Frames.Single(static registration => registration.Format == "BLF");
        using SettingsManager manager = RegistrationOptions.CreateDefaults(blf.RegisterOptions);

        Setting container = Expect.NotNull(manager.GetSetting("containerSize"));
        string description = Expect.NotNull(container.Description);

        await Assert.That(Expect.NotNull(manager.GetSetting("writeMode"))).IsNotNull();
        await Assert.That(manager.GetI64Setting("containerSize")).IsEqualTo(10L * 1024 * 1024);
        await Assert.That(description).Contains("65536");
        await Assert.That(description).Contains("67108864");
        await Assert.That(() => OptionManager.Set(manager, "containerSize", SettingValue.I64(65_535))).Throws<ValidationSettingsException>();
        await Assert.That(() => OptionManager.Set(manager, "containerSize", SettingValue.I64(67_108_865))).Throws<ValidationSettingsException>();
    }

    [Test]
    public async Task TheBatchingDescriptionsStateUnitRangeDefaultAndWhy()
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(_Packet("DuckDB").RegisterOptions);

        string packets = Expect.NotNull(Expect.NotNull(manager.GetSetting("batchPackets")).Description);
        string bytes = Expect.NotNull(Expect.NotNull(manager.GetSetting("batchBytes")).Description);

        await Assert.That(packets).Contains("1 to 2147483647");
        await Assert.That(packets).Contains("default 50000");
        await Assert.That(packets).Contains("memory");
        await Assert.That(bytes).Contains("9223372036854775807");
        await Assert.That(bytes).Contains("16777216");
        await Assert.That(manager.GetI64Setting("batchPackets")).IsEqualTo(50_000L);
        await Assert.That(manager.GetI64Setting("batchBytes")).IsEqualTo(16L * 1024 * 1024);
    }

    #endregion

    #region Values reach the exporter

    [Test]
    public async Task TheFieldListWithAnOccurrenceChoiceReachesTheCsvOutput()
    {
        using TempFolder folder = new("opt-csv-fields");
        Stack stack = _Stack();
        List<Frame> frames = SmallTraceWriter.Generate(3, 7, stack.FrameInterfaceRegistry);
        List<Packet> packets = [.. frames.Select(frame => SampleStack.Parse(stack, frame))];
        string target = _Target(folder, "CSV", "a");

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), target, stack,
            ("fields", SettingValue.String("ip.src:last;udp.dstport")), ("separator", SettingValue.String(";")),
            ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)), ("bom", SettingValue.Bool(false)));

        await Assert.That(error).IsNull();
        foreach (Packet packet in packets)
        {
            await Assert.That(Expect.NotNull(listener).OnPacket(packet)).IsTrue();
        }

        Expect.NotNull(listener).OnFinish();
        _Dispose(listener);
        string[] lines = File.ReadAllLines(target);
        ulong port = Expect.NotNull(SampleStack.U64(stack, packets[0], "udp.dstport"));

        // The registration wrote the wide layout: packet columns, then the two listed fields in the listed order.
        await Assert.That(lines[0]).IsEqualTo("No.,Time,ip.src,udp.dstport");
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(lines[1].EndsWith("," + port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ShapeAutoPicksFieldsWhenListedAndTheFormatDefaultOtherwise()
    {
        using TempFolder folder = new("opt-auto");
        Stack stack = _Stack();

        (IPacketListener? withFields, PluginError? e1) = RegistrationRunner.Create(_Packet("DuckDB"), _Target(folder, "DuckDB", "a"), stack,
            ("fields", SettingValue.String("udp.dstport")));
        (IPacketListener? withoutFields, PluginError? e2) = RegistrationRunner.Create(_Packet("DuckDB"), _Target(folder, "DuckDB", "b"), stack);

        // The selected-fields exporter demands a tree-free parse; the default hierarchy demands the tree.
        ParseDemand selected = ((IParseDemandSource)Expect.NotNull(withFields)).ParseDemand;
        ParseDemand hierarchy = ((IParseDemandSource)Expect.NotNull(withoutFields)).ParseDemand;
        _Dispose(withFields);
        _Dispose(withoutFields);

        await Assert.That(e1).IsNull();
        await Assert.That(e2).IsNull();
        await Assert.That(selected.Depth).IsEqualTo(ParseDepth.Packet);
        await Assert.That(hierarchy.Depth).IsEqualTo(ParseDepth.FieldTree);
    }

    [Test]
    public async Task TheCsvPacketsShapeIsTheDefaultAndDemandsNoFieldTree()
    {
        using TempFolder folder = new("opt-csv-default");
        Stack stack = _Stack();

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), _Target(folder, "CSV", "a"), stack);
        ParseDemand demand = ((IParseDemandSource)Expect.NotNull(listener)).ParseDemand;
        _Dispose(listener);

        await Assert.That(error).IsNull();
        await Assert.That(demand.Depth).IsEqualTo(ParseDepth.Packet);
        await Assert.That(demand.Observer.Observer).IsNull();
    }

    /// <summary>The PBF registration builds an exporter for the standard layout, with the default content and with <c>frames=true</c>.</summary>
    [Test]
    public async Task APbfStandardLayoutBuildsWithTheDefaultsAndWithFrames()
    {
        using TempFolder folder = new("opt-pbf-standard");
        Stack stack = _Stack();

        (IPacketListener? standard, PluginError? error) = RegistrationRunner.Create(_Packet("PBF"), _Target(folder, "PBF", "a"), stack,
            ("format", SettingValue.Enum("standard", 0)));
        (IPacketListener? withFrames, PluginError? framesError) = RegistrationRunner.Create(_Packet("PBF"), _Target(folder, "PBF", "b"), stack,
            ("format", SettingValue.Enum("standard", 0)), ("frames", SettingValue.Bool(true)));
        _Dispose(standard);
        _Dispose(withFrames);

        await Assert.That(error).IsNull();
        await Assert.That(standard).IsNotNull();
        await Assert.That(framesError).IsNull();
        await Assert.That(withFrames).IsNotNull();
    }

    #endregion

    #region Frames

    /// <summary>
    /// Every packet exporter registers the <c>frames</c> option as a switch that is off by default, and its description says that
    /// it is off by default and what the frame copy costs without a field tree.
    /// </summary>
    /// <param name="format">The packet exporter format.</param>
    [Test]
    [Arguments("CSV")]
    [Arguments("JSON")]
    [Arguments("Text")]
    [Arguments("PBF")]
    [Arguments("Parquet")]
    [Arguments("DuckDB")]
    public async Task TheFramesOptionIsOffByDefaultAndItsDescriptionSaysWhy(string format)
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(_Packet(format).RegisterOptions);

        Setting frames = Expect.NotNull(manager.GetSetting("frames"));
        string description = Expect.NotNull(frames.Description);

        await Assert.That(manager.GetBoolSetting("frames") is false).IsTrue();
        await Assert.That(description).Contains("off by default");
        await Assert.That(description).Contains("field tree");
    }

    /// <summary>The frame exporters (PCAPNG, BLF, ASC) write frames themselves, so they register no <c>frames</c> option.</summary>
    /// <param name="format">The frame exporter format.</param>
    [Test]
    [Arguments("PCAPNG")]
    [Arguments("BLF")]
    [Arguments("ASC")]
    public async Task TheFrameExportersHaveNoFramesOption(string format)
    {
        ExporterRegistration<IFrameListener> registration = _Registrations().Frames.Single(r => r.Format == format);

        string[] names = _OptionNames(registration.RegisterOptions);

        // They write the frames themselves, so a switch for the frame copy of a packet export has no meaning there.
        await Assert.That(names.Contains("frames")).IsFalse();
    }

    /// <summary>
    /// Runs the registration of <paramref name="format"/> with <paramref name="options"/> over three random packets, finishes the
    /// exporter, and returns the target path and the frames that were exported. A listener that refuses a packet fails the test
    /// with a message instead of leaving a short file behind.
    /// </summary>
    /// <param name="folder">The folder that holds the target.</param>
    /// <param name="format">The exporter format, for example <c>CSV</c>.</param>
    /// <param name="name">The target name inside <paramref name="folder"/>; a different name per call keeps the files apart.</param>
    /// <param name="options">The option values to apply on top of the defaults.</param>
    /// <returns>The target path and the three exported frames.</returns>
    private static (string Target, List<Frame> Frames) _WriteThreePackets(TempFolder folder, string format, string name,
        params (string Name, SettingValue Value)[] options)
    {
        Stack stack = _Stack();
        List<Frame> frames = SmallTraceWriter.Generate(3, 11, stack.FrameInterfaceRegistry);
        List<Packet> packets = [.. frames.Select(frame => SampleStack.Parse(stack, frame))];
        string target = _Target(folder, format, name);
        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet(format), target, stack, options);
        IPacketListener created = listener ?? throw new InvalidOperationException(Expect.NotNull(error).Message);
        foreach (Packet packet in packets)
        {
            if (!created.OnPacket(packet))
            {
                throw new InvalidOperationException("The exporter stopped accepting packets.");
            }
        }

        created.OnFinish();
        _Dispose(created);
        return (target, frames);
    }

    /// <summary>
    /// With <c>frames=true</c> the JSON records carry the frame as Base64 under <c>frame_bytes</c> and the Text packets list a
    /// <c>Frame bytes</c> line; without the option neither file mentions the frame.
    /// </summary>
    [Test]
    public async Task TheFramesOptionWritesTheFrameInJsonAndTextAndNothingWithoutIt()
    {
        using TempFolder folder = new("opt-frames-text");

        (string jsonOn, List<Frame> frames) = _WriteThreePackets(folder, "JSON", "a", ("frames", SettingValue.Bool(true)));
        (string jsonOff, _) = _WriteThreePackets(folder, "JSON", "b");
        (string textOn, _) = _WriteThreePackets(folder, "Text", "a", ("frames", SettingValue.Bool(true)));
        (string textOff, _) = _WriteThreePackets(folder, "Text", "b");

        using JsonDocument withFrames = JsonDocument.Parse(File.ReadAllText(jsonOn));
        string base64 = Convert.ToBase64String(frames[0].Data.Span);

        // The default style is compact lines; the frame key is spelled the same in every style.
        await Assert.That(withFrames.RootElement[0].GetProperty("frame_bytes").GetString()).IsEqualTo(base64);
        await Assert.That(File.ReadAllText(jsonOff).Contains("frame_bytes", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(textOn).Contains(Invariant($"Frame bytes ({frames[0].Data.Length}):"), StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.ReadAllText(textOff).Contains("Frame bytes", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// In both PBF layouts <c>frames=true</c> makes the uncompressed file larger by the three frames plus a few bytes of tags and
    /// lengths, and nothing else.
    /// </summary>
    /// <param name="layout">The PBF layout, <c>standard</c> or <c>columnar</c>.</param>
    [Test]
    [Arguments("standard")]
    [Arguments("columnar")]
    public async Task TheFramesOptionWritesTheRawFrameInBothPbfLayoutsAndNothingWithoutIt(string layout)
    {
        using TempFolder folder = new("opt-frames-pbf");
        SettingValue format = SettingValue.Enum(layout, layout == "standard" ? 0UL : 1UL);

        (string on, List<Frame> frames) = _WriteThreePackets(folder, "PBF", "a",
            ("format", format), ("compressed", SettingValue.Bool(false)), ("frames", SettingValue.Bool(true)));
        (string off, _) = _WriteThreePackets(folder, "PBF", "b", ("format", format), ("compressed", SettingValue.Bool(false)));

        // The field tree holds the layer bytes, so the frame is found in the file with and without the option; the option adds exactly
        // the copy: the file is larger by the frames plus a few bytes of tags and lengths (the blocks are uncompressed).
        long frameBytes = frames.Sum(static frame => (long)frame.Data.Length);
        long added = new FileInfo(on).Length - new FileInfo(off).Length;

        await Assert.That(added).IsGreaterThanOrEqualTo(frameBytes);
        await Assert.That(added).IsLessThan(frameBytes + 64);
    }

    /// <summary>Runs a counting query on a DuckDB database and returns the number that DuckDB itself counted.</summary>
    /// <param name="dataSource">A DuckDB file path, or <c>:memory:</c> to query a Parquet file.</param>
    /// <param name="sql">A query that returns one number.</param>
    /// <returns>The single value of the query.</returns>
    private static long _Count(string dataSource, string sql)
    {
        using DuckDB.NET.Data.DuckDBConnection connection = new(Invariant($"Data Source={dataSource}"));
        connection.Open();
        using DuckDB.NET.Data.DuckDBCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// With <c>frames=true</c> the DuckDB <c>packets</c> table and the Parquet <c>packets</c> file have a <c>frame_bytes</c> column;
    /// without it neither has one. Slow: it writes four database files.
    /// </summary>
    [Test]
    [Category("Slow")]
    public async Task TheFramesOptionAddsTheFrameColumnToDuckDbAndParquetAndNotWithoutIt()
    {
        using TempFolder folder = new("opt-frames-db");

        (string duckOn, _) = _WriteThreePackets(folder, "DuckDB", "a", ("frames", SettingValue.Bool(true)), ("writeMode", SettingValue.Enum("sync", 1)));
        (string duckOff, _) = _WriteThreePackets(folder, "DuckDB", "b", ("writeMode", SettingValue.Enum("sync", 1)));
        (string parquetOn, _) = _WriteThreePackets(folder, "Parquet", "a", ("frames", SettingValue.Bool(true)), ("writeMode", SettingValue.Enum("sync", 1)));
        (string parquetOff, _) = _WriteThreePackets(folder, "Parquet", "b", ("writeMode", SettingValue.Enum("sync", 1)));

        // A DuckDB file is opened to read its catalog; the schema of a Parquet file is read through parquet_schema.
        const string DuckDbQuery = "SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'packets' AND column_name = 'frame_bytes'";
        string parquetQueryOn = Invariant($"SELECT COUNT(*) FROM parquet_schema('{Path.Combine(parquetOn, "packets.parquet")}') WHERE name = 'frame_bytes'");
        string parquetQueryOff = Invariant($"SELECT COUNT(*) FROM parquet_schema('{Path.Combine(parquetOff, "packets.parquet")}') WHERE name = 'frame_bytes'");

        await Assert.That(_Count(duckOn, DuckDbQuery)).IsEqualTo(1L);
        await Assert.That(_Count(duckOff, DuckDbQuery)).IsEqualTo(0L);
        await Assert.That(_Count(":memory:", parquetQueryOn)).IsEqualTo(1L);
        await Assert.That(_Count(":memory:", parquetQueryOff)).IsEqualTo(0L);
    }

    /// <summary>
    /// With <c>frames=true</c> the CSV packet rows keep the default columns (No., Time, Info, Length) and add <c>frame_bytes</c>
    /// last, holding the upper-case hex of the frame without separators.
    /// </summary>
    [Test]
    public async Task TheCsvFramesOptionKeepsTheDefaultPacketColumnsAndAddsTheFrameColumnLast()
    {
        using TempFolder folder = new("opt-frames-csv-rows");

        (string target, List<Frame> frames) = _WriteThreePackets(folder, "CSV", "a",
            ("frames", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)));
        string[] lines = File.ReadAllLines(target);

        await Assert.That(lines[0]).IsEqualTo("No.,Time,Info,Length,frame_bytes");
        await Assert.That(lines.Length).IsEqualTo(4);
        await Assert.That(lines[1].EndsWith("," + Convert.ToHexString(frames[0].Data.Span), StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// In the selected-fields (wide) CSV layout <c>frames=true</c> puts <c>frame_bytes</c> after No. and Time and before the listed
    /// fields; the cell holds the hex of the frame.
    /// </summary>
    [Test]
    public async Task TheCsvFramesOptionOfTheWideLayoutWritesNumberAndTimeThenTheFrameColumnThenTheFields()
    {
        using TempFolder folder = new("opt-frames-csv-wide");

        (string target, List<Frame> frames) = _WriteThreePackets(folder, "CSV", "a",
            ("fields", SettingValue.String("udp.dstport")), ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)),
            ("frames", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)));
        string[] lines = File.ReadAllLines(target);
        string[] cells = lines[1].Split(',');

        await Assert.That(lines[0]).IsEqualTo("No.,Time,frame_bytes,udp.dstport");
        await Assert.That(cells[2]).IsEqualTo(Convert.ToHexString(frames[0].Data.Span));
    }

    /// <summary>
    /// A CSV long layout (<c>all</c> or <c>hierarchy</c>) has no packet row to hold the frame, so <c>frames=true</c> is refused
    /// with an error that names the option, and no file is created.
    /// </summary>
    /// <param name="shape">The long layout.</param>
    [Test]
    [Arguments("all")]
    [Arguments("hierarchy")]
    public async Task TheCsvFramesOptionWithALongLayoutIsRefusedNamingTheOptionAndNoFileIsCreated(string shape)
    {
        using TempFolder folder = new("opt-frames-csv-long");
        Stack stack = _Stack();
        string target = _Target(folder, "CSV", "a");

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), target, stack,
            ("shape", SettingValue.Enum(shape, shape == "all" ? 2UL : 3UL)), ("frames", SettingValue.Bool(true)));
        _Dispose(listener);

        await Assert.That(listener).IsNull();
        await Assert.That(error!.Value.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(error.Value.Option).IsEqualTo("frames");
        string shapeName = shape == "all"
            ? "AllFields"
            : "Hierarchy";
        await Assert.That(error.Value.Message).IsEqualTo(
            Invariant($"The FrameBytes column is only valid in packet rows and the selected-fields layout, not with the shape {shapeName}."));
        await Assert.That(File.Exists(target)).IsFalse();
    }

    #endregion

    #region Buffers

    /// <summary>
    /// Every packet exporter registers the <c>buffers</c> option as a switch that is off by default, and its description names the
    /// content (reassembled datagrams, decoded HTTP bodies) and warns about disclosure and size.
    /// </summary>
    /// <param name="format">The packet exporter format.</param>
    [Test]
    [Arguments("CSV")]
    [Arguments("JSON")]
    [Arguments("Text")]
    [Arguments("PBF")]
    [Arguments("Parquet")]
    [Arguments("DuckDB")]
    public async Task TheBuffersOptionIsOffByDefaultAndItsDescriptionNamesTheContentAndTheSize(string format)
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(_Packet(format).RegisterOptions);

        Setting buffers = Expect.NotNull(manager.GetSetting("buffers"));
        string description = Expect.NotNull(buffers.Description);

        await Assert.That(manager.GetBoolSetting("buffers") is false).IsTrue();
        await Assert.That(description).Contains("off by default");
        await Assert.That(description).Contains("reassembled datagrams");
        await Assert.That(description).Contains("decoded HTTP bodies");

        // The information-disclosure and size warning that the option help owes the user.
        await Assert.That(description).Contains("decoded content that the frame does not show in clear");
        await Assert.That(description).Contains("much larger than the frames");
    }

    /// <summary>The frame exporters (PCAPNG, BLF, ASC) write frames, not packet content, so they register neither <c>buffers</c> nor <c>frames</c>.</summary>
    /// <param name="format">The frame exporter format.</param>
    [Test]
    [Arguments("PCAPNG")]
    [Arguments("BLF")]
    [Arguments("ASC")]
    public async Task TheFrameExportersHaveNoBuffersOption(string format)
    {
        ExporterRegistration<IFrameListener> registration = _Registrations().Frames.Single(r => r.Format == format);

        string[] names = _OptionNames(registration.RegisterOptions);

        // They write frames, not packet content, so a switch for the buffers of a packet has no meaning there (the CLI refuses it as
        // an unknown option).
        await Assert.That(names.Contains("buffers")).IsFalse();
        await Assert.That(names.Contains("frames")).IsFalse();
    }

    /// <summary>
    /// The frames of a datagram that two IPv4 fragments carry (UDP over IPv4 behind an Ethernet header): the second frame completes
    /// the datagram, so its packet has one additional buffer, the reassembled UDP datagram, which is also returned.
    /// </summary>
    /// <param name="stack">The stack whose frame interface registry the frames belong to.</param>
    /// <returns>The two frames and the reassembled datagram.</returns>
    /// <remarks>
    /// Sync: <c>CliTraceWriter.WriteFragmentedUdpPcapng</c> in NetworkInspector.CLI.Tests (the same layout of two IPv4 fragments
    /// of one UDP datagram, written to a capture file instead of frame objects; the payload values and addresses differ).
    /// </remarks>
    private static (List<Frame> Frames, byte[] Datagram) _FragmentFrames(Stack stack)
    {
        byte[] datagram = new byte[8 + 24];
        BinaryPrimitives.WriteUInt16BigEndian(datagram, 40000);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2), 40001);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(4), (ushort)datagram.Length);
        for (int i = 8; i < datagram.Length; i++)
        {
            datagram[i] = (byte)i;
        }

        // The interface of the frames comes from a generated frame, so the frames belong to the stack's registry.
        FrameInterfaceId interfaceId = SmallTraceWriter.Generate(1, 5, stack.FrameInterfaceRegistry)[0].InterfaceId;
        List<Frame> frames = [];
        for (int part = 0; part < 2; part++)
        {
            ReadOnlySpan<byte> payload = part == 0
                ? datagram.AsSpan(0, 16)
                : datagram.AsSpan(16);
            byte[] bytes = new byte[14 + 20 + payload.Length];
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800);
            bytes[14] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), (ushort)(20 + payload.Length));
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), 0x1234);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), (ushort)(part == 0 ? 0x2000 : 2));
            bytes[22] = 64;
            bytes[23] = 17;
            bytes[26] = 10;
            bytes[29] = 1;
            bytes[30] = 10;
            bytes[33] = 2;
            payload.CopyTo(bytes.AsSpan(34));
            frames.Add(Frame.Create(
                part, Timestamp.FromNanos(1_000_000 * (part + 1)), bytes, LinkType.Ethernet, interfaceId, stack.FrameInterfaceRegistry).Value);
        }

        return (frames, datagram);
    }

    /// <summary>
    /// Runs the registration of <paramref name="format"/> with <paramref name="options"/> over the two fragment frames of
    /// <see cref="_FragmentFrames"/>, finishes the exporter, and returns the target path and the reassembled datagram. A listener
    /// that refuses a packet fails the test with a message.
    /// </summary>
    /// <param name="folder">The folder that holds the target.</param>
    /// <param name="format">The exporter format, for example <c>JSON</c>.</param>
    /// <param name="name">The target name inside <paramref name="folder"/>; a different name per call keeps the files apart.</param>
    /// <param name="options">The option values to apply on top of the defaults.</param>
    /// <returns>The target path and the reassembled UDP datagram that the second packet carries as its buffer.</returns>
    private static (string Target, byte[] Datagram) _WriteFragments(TempFolder folder, string format, string name,
        params (string Name, SettingValue Value)[] options)
    {
        Stack stack = _Stack();
        (List<Frame> frames, byte[] datagram) = _FragmentFrames(stack);
        List<Packet> packets = [.. frames.Select(frame => SampleStack.Parse(stack, frame))];
        string target = _Target(folder, format, name);
        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet(format), target, stack, options);
        IPacketListener created = listener ?? throw new InvalidOperationException(Expect.NotNull(error).Message);
        foreach (Packet packet in packets)
        {
            if (!created.OnPacket(packet))
            {
                throw new InvalidOperationException("The exporter stopped accepting packets.");
            }
        }

        created.OnFinish();
        _Dispose(created);
        return (target, datagram);
    }

    /// <summary>
    /// With <c>buffers=true</c> the JSON record, the Text packet, and the CSV row of the packet that completed the datagram carry the
    /// reassembled datagram under buffer index 1, and the packet without a buffer carries nothing; without the option no file
    /// mentions a buffer.
    /// </summary>
    [Test]
    public async Task TheBuffersOptionWritesTheReassembledDatagramInJsonTextAndCsvAndNothingWithoutIt()
    {
        using TempFolder folder = new("opt-buffers-text");

        (string jsonOn, byte[] datagram) = _WriteFragments(folder, "JSON", "a", ("buffers", SettingValue.Bool(true)));
        (string jsonOff, _) = _WriteFragments(folder, "JSON", "b");
        (string textOn, _) = _WriteFragments(folder, "Text", "a", ("buffers", SettingValue.Bool(true)));
        (string textOff, _) = _WriteFragments(folder, "Text", "b");
        (string csvOn, _) = _WriteFragments(folder, "CSV", "a", ("buffers", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)));
        (string csvOff, _) = _WriteFragments(folder, "CSV", "b", ("bom", SettingValue.Bool(false)));

        using JsonDocument withBuffers = JsonDocument.Parse(File.ReadAllText(jsonOn));
        JsonElement record = withBuffers.RootElement[1].GetProperty("buffers")[0];
        string[] csvLines = File.ReadAllLines(csvOn);

        // Only the packet that completed the datagram has a buffer: index 1, the reassembled datagram.
        await Assert.That(withBuffers.RootElement[0].TryGetProperty("buffers", out _)).IsFalse();
        await Assert.That(record.GetProperty("index").GetInt32()).IsEqualTo(1);
        await Assert.That(record.GetProperty("bytes").GetString()).IsEqualTo(Convert.ToBase64String(datagram));
        await Assert.That(File.ReadAllText(jsonOff).Contains("buffers", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.ReadAllText(textOn).Contains(Invariant($"Buffer 1 ({datagram.Length}):"), StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.ReadAllText(textOff).Contains("Buffer 1", StringComparison.Ordinal)).IsFalse();
        await Assert.That(csvLines[0]).IsEqualTo("No.,Time,Info,Length,buffers");
        await Assert.That(csvLines[1].EndsWith(',')).IsTrue();
        await Assert.That(csvLines[2].EndsWith(",1:" + Convert.ToHexString(datagram), StringComparison.Ordinal)).IsTrue();
        await Assert.That(File.ReadAllText(csvOff).Contains("buffers", StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// In both PBF layouts <c>buffers=true</c> makes the uncompressed file larger by the reassembled datagram plus a few bytes of
    /// tags, lengths, and index, and nothing else.
    /// </summary>
    /// <param name="layout">The PBF layout, <c>standard</c> or <c>columnar</c>.</param>
    [Test]
    [Arguments("standard")]
    [Arguments("columnar")]
    public async Task TheBuffersOptionWritesTheDatagramInBothPbfLayoutsAndNothingWithoutIt(string layout)
    {
        using TempFolder folder = new("opt-buffers-pbf");
        SettingValue format = SettingValue.Enum(layout, layout == "standard" ? 0UL : 1UL);

        (string on, byte[] datagram) = _WriteFragments(folder, "PBF", "a",
            ("format", format), ("compressed", SettingValue.Bool(false)), ("buffers", SettingValue.Bool(true)));
        (string off, _) = _WriteFragments(folder, "PBF", "b", ("format", format), ("compressed", SettingValue.Bool(false)));

        // The option adds exactly the buffer record: the file is larger by the datagram plus a few bytes of tags, lengths, and index.
        long added = new FileInfo(on).Length - new FileInfo(off).Length;

        await Assert.That(added).IsGreaterThanOrEqualTo(datagram.Length);
        await Assert.That(added).IsLessThan(datagram.Length + 64);
    }

    /// <summary>
    /// With <c>buffers=true</c> DuckDB gets a <c>packet_buffers</c> table with the datagram row and Parquet a
    /// <c>packet_buffers.parquet</c> file with it; without the option neither exists. Slow: it writes four database files.
    /// </summary>
    [Test]
    [Category("Slow")]
    public async Task TheBuffersOptionAddsTheBufferTableToDuckDbAndTheFileToParquetAndNotWithoutIt()
    {
        using TempFolder folder = new("opt-buffers-db");

        (string duckOn, byte[] datagram) = _WriteFragments(folder, "DuckDB", "a",
            ("buffers", SettingValue.Bool(true)), ("writeMode", SettingValue.Enum("sync", 1)));
        (string duckOff, _) = _WriteFragments(folder, "DuckDB", "b", ("writeMode", SettingValue.Enum("sync", 1)));
        (string parquetOn, _) = _WriteFragments(folder, "Parquet", "a",
            ("buffers", SettingValue.Bool(true)), ("writeMode", SettingValue.Enum("sync", 1)));
        (string parquetOff, _) = _WriteFragments(folder, "Parquet", "b", ("writeMode", SettingValue.Enum("sync", 1)));

        const string TableQuery = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'packet_buffers'";
        string rowQuery = Invariant($"SELECT COUNT(*) FROM packet_buffers WHERE buffer_index = 1 AND octet_length(bytes) = {datagram.Length}");
        string parquetFile = Path.Combine(parquetOn, "packet_buffers.parquet");
        string parquetRows = Invariant(
            $"SELECT COUNT(*) FROM read_parquet('{parquetFile}') WHERE buffer_index = 1 AND octet_length(bytes) = {datagram.Length}");

        await Assert.That(_Count(duckOn, TableQuery)).IsEqualTo(1L);
        await Assert.That(_Count(duckOn, rowQuery)).IsEqualTo(1L);
        await Assert.That(_Count(duckOff, TableQuery)).IsEqualTo(0L);
        await Assert.That(_Count(":memory:", parquetRows)).IsEqualTo(1L);
        await Assert.That(File.Exists(Path.Combine(parquetOff, "packet_buffers.parquet"))).IsFalse();
    }

    /// <summary>
    /// With both <c>frames=true</c> and <c>buffers=true</c> the CSV packet rows add <c>frame_bytes</c> and then <c>buffers</c> after
    /// the default columns.
    /// </summary>
    [Test]
    public async Task TheCsvOptionsFramesAndBuffersTogetherAddTheFrameColumnThenTheBuffersColumnToTheDefaultColumns()
    {
        using TempFolder folder = new("opt-buffers-csv-both");

        (string target, byte[] datagram) = _WriteFragments(folder, "CSV", "a",
            ("frames", SettingValue.Bool(true)), ("buffers", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)));
        string[] lines = File.ReadAllLines(target);

        await Assert.That(lines[0]).IsEqualTo("No.,Time,Info,Length,frame_bytes,buffers");
        await Assert.That(lines[2].EndsWith(",1:" + Convert.ToHexString(datagram), StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// In the selected-fields (wide) CSV layout <c>buffers=true</c> puts the <c>buffers</c> column after No. and Time and before
    /// the listed fields.
    /// </summary>
    [Test]
    public async Task TheCsvBuffersOptionOfTheWideLayoutWritesNumberAndTimeThenTheBuffersColumnThenTheFields()
    {
        using TempFolder folder = new("opt-buffers-csv-wide");

        (string target, _) = _WriteFragments(folder, "CSV", "a",
            ("fields", SettingValue.String("udp.dstport")), ("text", SettingValue.Bool(false)), ("repr", SettingValue.Bool(false)),
            ("buffers", SettingValue.Bool(true)), ("bom", SettingValue.Bool(false)));

        await Assert.That(File.ReadAllLines(target)[0]).IsEqualTo("No.,Time,buffers,udp.dstport");
    }

    /// <summary>
    /// A CSV long layout (<c>all</c> or <c>hierarchy</c>) has no packet row to hold the buffers, so <c>buffers=true</c> is refused
    /// with an error that names the option, and no file is created.
    /// </summary>
    /// <param name="shape">The long layout.</param>
    [Test]
    [Arguments("all")]
    [Arguments("hierarchy")]
    public async Task TheCsvBuffersOptionWithALongLayoutIsRefusedNamingTheOptionAndNoFileIsCreated(string shape)
    {
        using TempFolder folder = new("opt-buffers-csv-long");
        Stack stack = _Stack();
        string target = _Target(folder, "CSV", "a");

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), target, stack,
            ("shape", SettingValue.Enum(shape, shape == "all" ? 2UL : 3UL)), ("buffers", SettingValue.Bool(true)));
        _Dispose(listener);

        await Assert.That(listener).IsNull();
        await Assert.That(error!.Value.Code).IsEqualTo(PluginErrorCode.InvalidOption);
        await Assert.That(error.Value.Option).IsEqualTo("buffers");
        string shapeName = shape == "all"
            ? "AllFields"
            : "Hierarchy";
        await Assert.That(error.Value.Message).IsEqualTo(
            Invariant($"The Buffers column is only valid in packet rows and the selected-fields layout, not with the shape {shapeName}."));
        await Assert.That(File.Exists(target)).IsFalse();
    }

    #endregion

    #region Bad values

    [Test]
    [Arguments("ip.scr", "Unknown field 'ip.scr' in option 'fields'.", "fields")]
    [Arguments("can.flags", "Field 'can.flags' has no value", "fields")]
    [Arguments("ip.src;ip.src", "Field 'ip.src' is listed twice in option 'fields'.", "fields")]
    [Arguments("ip.src:middle", "Unknown occurrence 'middle' for field 'ip.src' in option 'fields'; use all, first, or last.", "fields")]
    public async Task ABadFieldListIsRefusedWithAMessageThatNamesTheFieldAndNoFileIsCreated(string fields, string message, string option)
    {
        using TempFolder folder = new("opt-bad-fields");
        Stack stack = _Stack();

        foreach (string format in new[] { "CSV", "JSON", "Text", "PBF", "Parquet", "DuckDB" })
        {
            string target = _Target(folder, format, "x" + format);
            (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet(format), target, stack,
                ("fields", SettingValue.String(fields)));
            _Dispose(listener);

            await Assert.That(listener).IsNull();
            await Assert.That(error!.Value.Code).IsEqualTo(PluginErrorCode.InvalidOption);
            await Assert.That(error!.Value.Message).Contains(message);
            await Assert.That(error.Value.Option).IsEqualTo(option);
            await Assert.That(File.Exists(target) || Directory.Exists(target)).IsFalse();
        }
    }

    [Test]
    public async Task FieldsWithAShapeOtherThanFieldsAndFieldsShapeWithoutFieldsAreRefused()
    {
        using TempFolder folder = new("opt-bad-shape");
        Stack stack = _Stack();

        (IPacketListener? conflicting, PluginError? conflict) = RegistrationRunner.Create(_Packet("DuckDB"), _Target(folder, "DuckDB", "a"), stack,
            ("fields", SettingValue.String("ip.src")), ("shape", SettingValue.Enum("all", 2)));
        (IPacketListener? empty, PluginError? missing) = RegistrationRunner.Create(_Packet("DuckDB"), _Target(folder, "DuckDB", "b"), stack,
            ("shape", SettingValue.Enum("fields", 1)));
        _Dispose(conflicting);
        _Dispose(empty);

        await Assert.That(conflicting).IsNull();
        await Assert.That(conflict!.Value.Message).Contains("Option 'fields' needs shape=fields (or auto), not 'all'.");
        await Assert.That(empty).IsNull();
        await Assert.That(missing!.Value.Message).Contains("needs at least one field in option 'fields'");
    }

    [Test]
    [Arguments("")]
    [Arguments("a\nb")]
    [Arguments("a\rb")]
    public async Task ACsvSeparatorWithoutTextOrWithALineBreakIsRefusedNamingTheOption(string separator)
    {
        using TempFolder folder = new("opt-bad-separator");
        Stack stack = _Stack();

        (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet("CSV"), _Target(folder, "CSV", "a"), stack,
            ("separator", SettingValue.String(separator)));
        _Dispose(listener);

        await Assert.That(listener).IsNull();
        await Assert.That(error!.Value.Option).IsEqualTo("separator");
        await Assert.That(error.Value.Message).Contains("Option 'separator'");
    }

    [Test]
    public async Task BatchValuesOutsideTheRangeAreStoppedByTheSettings()
    {
        using TempFolder folder = new("opt-bad-batch");
        Stack stack = _Stack();

        foreach (string format in new[] { "PBF", "Parquet", "DuckDB" })
        {
            await Assert.That(() => RegistrationRunner.Create(_Packet(format), _Target(folder, format, "a"), stack,
                ("batchPackets", SettingValue.I64(0)))).Throws<ValidationSettingsException>();
            await Assert.That(() => RegistrationRunner.Create(_Packet(format), _Target(folder, format, "b"), stack,
                ("batchPackets", SettingValue.I64(2_147_483_648)))).Throws<ValidationSettingsException>();
            await Assert.That(() => RegistrationRunner.Create(_Packet(format), _Target(folder, format, "c"), stack,
                ("batchBytes", SettingValue.I64(0)))).Throws<ValidationSettingsException>();
        }
    }

    [Test]
    public async Task ValidBatchValuesAndTheSyncWriteModeBuildTheExporter()
    {
        using TempFolder folder = new("opt-batch");
        Stack stack = _Stack();

        foreach (string format in new[] { "PBF", "Parquet", "DuckDB" })
        {
            (IPacketListener? listener, PluginError? error) = RegistrationRunner.Create(_Packet(format), _Target(folder, format, "a"), stack,
                ("batchPackets", SettingValue.I64(7)), ("batchBytes", SettingValue.I64(1_000_000)), ("writeMode", SettingValue.Enum("sync", 1)));
            _Dispose(listener);

            await Assert.That(error).IsNull();
            await Assert.That(listener).IsNotNull();
        }
    }

    #endregion

    #region DuckDB storage version

    /// <summary>Reads the storage version that DuckDB reports for a finished file: the <c>storage_version</c> tag of the attached database.</summary>
    /// <param name="path">The DuckDB file.</param>
    /// <returns>The reported version text, for example <c>v1.5.0+</c>.</returns>
    /// <remarks>Sync: <c>CliExportFormatTests._ReportedStorageVersion</c> in NetworkInspector.CLI.Tests (the same query).</remarks>
    private static string _ReportedStorageVersion(string path)
    {
        using DuckDB.NET.Data.DuckDBConnection connection = new(Invariant($"Data Source={path};access_mode=READ_ONLY"));
        connection.Open();
        using DuckDB.NET.Data.DuckDBCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tags['storage_version'] FROM duckdb_databases() WHERE NOT internal";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!; // Invariant: the file is the only user database.
    }

    /// <summary>
    /// The option lists exactly the six versions under their setting texts, with the numbers of <c>DuckDbStorageVersion</c> (so the
    /// registration can cast the number back), the default <c>v1.5.0</c>, and a description that names the default.
    /// </summary>
    [Test]
    public async Task TheDuckDbStorageVersionOptionListsTheSixVersionsAndDefaultsToV150()
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(_Packet("DuckDB").RegisterOptions);
        Setting setting = Expect.NotNull(manager.GetSetting("storageVersion"));
        EnumSettingMetadata metadata = Expect.NotNull(setting.EnumMetadata);
        string[] expected = ["v1.0.0", "v1.1.0", "v1.2.0", "v1.3.0", "v1.4.0", "v1.5.0"];

        await Assert.That(metadata.AllowedValues.Select(static value => value.Name).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(metadata.AllowedValues.Select(static value => value.NumericValue).ToArray())
            .IsEquivalentTo(Enum.GetValues<DuckDbStorageVersion>().Select(static version => (ulong)version).ToArray(), CollectionOrdering.Matching);
        await Assert.That(manager.GetEnumSetting("storageVersion")?.Name).IsEqualTo("v1.5.0");
        await Assert.That(Expect.NotNull(setting.Description)).Contains("Default v1.5.0");
    }

    /// <summary>
    /// Each option text reaches the file: DuckDB reports the format of the chosen version (v1.0.0 and v1.1.0 share one format and are
    /// reported as <c>v1.0.0+</c>), and no option text maps to another version.
    /// </summary>
    [Test]
    [Category("Slow")]
    [Arguments("v1.0.0", 0UL, "v1.0.0+")]
    [Arguments("v1.1.0", 1UL, "v1.0.0+")]
    [Arguments("v1.2.0", 2UL, "v1.2.0+")]
    [Arguments("v1.3.0", 3UL, "v1.3.0+")]
    [Arguments("v1.4.0", 4UL, "v1.4.0+")]
    [Arguments("v1.5.0", 5UL, "v1.5.0+")]
    public async Task EveryStorageVersionTextReachesTheDuckDbFile(string text, ulong number, string reported)
    {
        using TempFolder folder = new("opt-storage-version");

        (string target, _) = _WriteThreePackets(folder, "DuckDB", "a",
            ("storageVersion", SettingValue.Enum(text, number)), ("writeMode", SettingValue.Enum("sync", 1)));

        await Assert.That(_ReportedStorageVersion(target)).IsEqualTo(reported);
    }

    /// <summary>The registration without the option writes the default <c>v1.5.0</c>, not the library default.</summary>
    [Test]
    [Category("Slow")]
    public async Task WithoutTheOptionTheDuckDbFileHasStorageVersionV150()
    {
        using TempFolder folder = new("opt-storage-default");

        (string target, _) = _WriteThreePackets(folder, "DuckDB", "a", ("writeMode", SettingValue.Enum("sync", 1)));

        await Assert.That(_ReportedStorageVersion(target)).IsEqualTo("v1.5.0+");
    }

    /// <summary>
    /// A text outside the six is refused by the enum setting, before the exporter is created: the message lists the allowed values and
    /// no file exists afterwards.
    /// </summary>
    [Test]
    public async Task AnUnknownStorageVersionIsRefusedWithTheAllowedValuesAndNoFileExists()
    {
        using TempFolder folder = new("opt-storage-bad");
        string target = _Target(folder, "DuckDB", "a");

        ValidationSettingsException unknown = Assert.Throws<ValidationSettingsException>(
            () => RegistrationRunner.Create(_Packet("DuckDB"), target, _Stack(), ("storageVersion", SettingValue.Enum("v9", 9))));

        await Assert.That(unknown.Message).Contains("v1.0.0, v1.1.0, v1.2.0, v1.3.0, v1.4.0, v1.5.0");
        await Assert.That(File.Exists(target)).IsFalse();
    }

    /// <summary>
    /// The export only appends and ends with the checkpoint, so the DuckDB registration offers no compaction, vacuum, or copy option
    /// (a compaction would gain little and add a failure point at the end).
    /// </summary>
    [Test]
    public async Task TheDuckDbOptionsHaveNoCompactionOption()
    {
        string[] names = _OptionNames(_Packet("DuckDB").RegisterOptions);

        foreach (string forbidden in new[] { "compact", "vacuum", "copy" })
        {
            await Assert.That(names.Any(name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))).IsFalse();
        }

        await Assert.That(names.Contains("storageVersion")).IsTrue();
    }

    #endregion

    #region Parquet compression

    /// <summary>Reads the distinct codecs that DuckDB reports over every column chunk of every Parquet file below the dataset directory.</summary>
    /// <param name="directory">The Parquet dataset directory.</param>
    /// <returns>The codec names in sorted order.</returns>
    /// <remarks>Sync: <c>CliExportFormatTests._ReportedCodecs</c> in NetworkInspector.CLI.Tests (the same query).</remarks>
    private static string[] _ReportedCodecs(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.parquet", SearchOption.AllDirectories);
        string list = string.Join(", ", files.Select(static file => "'" + file.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal) + "'"));
        using DuckDB.NET.Data.DuckDBConnection connection = new("Data Source=:memory:");
        connection.Open();
        using DuckDB.NET.Data.DuckDBCommand command = connection.CreateCommand();
        command.CommandText = Invariant($"SELECT DISTINCT compression FROM parquet_metadata([{list}]) ORDER BY 1");
        using DuckDB.NET.Data.DuckDBDataReader reader = command.ExecuteReader();
        List<string> codecs = [];
        while (reader.Read())
        {
            codecs.Add(reader.GetString(0));
        }

        return [.. codecs];
    }

    /// <summary>
    /// The options list exactly the six codecs and the three levels under their texts, with the numbers of the library enums (so the
    /// registration can cast the number back), the defaults zstd and optimal, and descriptions that name the trade-offs.
    /// </summary>
    [Test]
    public async Task TheParquetCompressionOptionsListTheCodecsAndLevelsAndDefaultToZstdOptimal()
    {
        using SettingsManager manager = RegistrationOptions.CreateDefaults(_Packet("Parquet").RegisterOptions);
        Setting codec = Expect.NotNull(manager.GetSetting("compression"));
        Setting level = Expect.NotNull(manager.GetSetting("compressionLevel"));

        await Assert.That(Expect.NotNull(codec.EnumMetadata).AllowedValues.Select(static value => value.Name).ToArray())
            .IsEquivalentTo(["none", "snappy", "gzip", "brotli", "lz4", "zstd"], CollectionOrdering.Matching);
        await Assert.That(Expect.NotNull(codec.EnumMetadata).AllowedValues.Select(static value => value.NumericValue).ToArray())
            .IsEquivalentTo(Enum.GetValues<ParquetCompression>().Select(static value => (ulong)value).ToArray(), CollectionOrdering.Matching);
        await Assert.That(Expect.NotNull(level.EnumMetadata).AllowedValues.Select(static value => value.Name).ToArray())
            .IsEquivalentTo(["fastest", "optimal", "smallest"], CollectionOrdering.Matching);
        await Assert.That(Expect.NotNull(level.EnumMetadata).AllowedValues.Select(static value => value.NumericValue).ToArray())
            .IsEquivalentTo(Enum.GetValues<ParquetCompressionLevel>().Select(static value => (ulong)value).ToArray(), CollectionOrdering.Matching);
        await Assert.That(manager.GetEnumSetting("compression")?.Name).IsEqualTo("zstd");
        await Assert.That(manager.GetEnumSetting("compressionLevel")?.Name).IsEqualTo("optimal");
        await Assert.That(Expect.NotNull(codec.Description)).Contains("LZ4_RAW");
        await Assert.That(Expect.NotNull(codec.Description)).Contains("not every tool reads it");
    }

    /// <summary>Each option text reaches every file of the dataset: DuckDB reports exactly that codec on every column chunk.</summary>
    [Test]
    [Category("Slow")]
    [Arguments("none", 0UL, "UNCOMPRESSED")]
    [Arguments("snappy", 1UL, "SNAPPY")]
    [Arguments("gzip", 2UL, "GZIP")]
    [Arguments("brotli", 3UL, "BROTLI")]
    [Arguments("lz4", 4UL, "LZ4_RAW")]
    [Arguments("zstd", 5UL, "ZSTD")]
    public async Task EveryCompressionTextReachesTheParquetDataset(string text, ulong number, string reported)
    {
        using TempFolder folder = new("opt-compression");

        (string target, _) = _WriteThreePackets(folder, "Parquet", "a",
            ("compression", SettingValue.Enum(text, number)), ("writeMode", SettingValue.Enum("sync", 1)));

        await Assert.That(_ReportedCodecs(target)).IsEquivalentTo([reported], CollectionOrdering.Matching);
    }

    /// <summary>A level text with zstd builds a readable dataset; the level options of the settings are accepted in full.</summary>
    [Test]
    [Category("Slow")]
    [Arguments("fastest", 0UL)]
    [Arguments("optimal", 1UL)]
    [Arguments("smallest", 2UL)]
    public async Task EveryCompressionLevelTextBuildsAZstdDataset(string text, ulong number)
    {
        using TempFolder folder = new("opt-compression-level");

        (string target, _) = _WriteThreePackets(folder, "Parquet", "a",
            ("compression", SettingValue.Enum("zstd", 5)), ("compressionLevel", SettingValue.Enum(text, number)),
            ("writeMode", SettingValue.Enum("sync", 1)));

        await Assert.That(_ReportedCodecs(target)).IsEquivalentTo(["ZSTD"], CollectionOrdering.Matching);
    }

    /// <summary>The registration without options writes zstd in every file, not the library default.</summary>
    [Test]
    [Category("Slow")]
    public async Task WithoutTheOptionsTheParquetDatasetIsZstd()
    {
        using TempFolder folder = new("opt-compression-default");

        (string target, _) = _WriteThreePackets(folder, "Parquet", "a", ("writeMode", SettingValue.Enum("sync", 1)));

        await Assert.That(_ReportedCodecs(target)).IsEquivalentTo(["ZSTD"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// A codec or level text outside the registered ones is refused by the enum setting, before the exporter is created: the message
    /// lists the allowed values and no directory exists afterwards.
    /// </summary>
    [Test]
    public async Task AnUnknownCodecOrLevelIsRefusedWithTheAllowedValuesAndNoDirectoryExists()
    {
        using TempFolder folder = new("opt-compression-bad");
        string target = _Target(folder, "Parquet", "a");

        ValidationSettingsException codec = Assert.Throws<ValidationSettingsException>(
            () => RegistrationRunner.Create(_Packet("Parquet"), target, _Stack(), ("compression", SettingValue.Enum("lzma", 9))));
        ValidationSettingsException level = Assert.Throws<ValidationSettingsException>(
            () => RegistrationRunner.Create(_Packet("Parquet"), target, _Stack(), ("compressionLevel", SettingValue.Enum("max", 9))));

        await Assert.That(codec.Message).Contains("none, snappy, gzip, brotli, lz4, zstd");
        await Assert.That(level.Message).Contains("fastest, optimal, smallest");
        await Assert.That(Directory.Exists(target)).IsFalse();
    }

    #endregion
}
