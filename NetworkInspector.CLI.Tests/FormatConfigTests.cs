// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Format specs resolved through the plugin registry: packet formats for export, frame formats for convert.</summary>
internal sealed class FormatConfigTests
{
    #region Helpers

    private static string _OptionText(IReadOnlyList<KeyValuePair<string, string>> texts) =>
        string.Join(",", texts.Select(static pair => pair.Key + "=" + pair.Value));

    #endregion

    #region Packet formats

    [Test]
    [Arguments("csv", "CSV")]
    [Arguments("json", "JSON")]
    [Arguments("json:style=pretty", "JSON")]
    [Arguments("pbf:format=columnar,compressed", "PBF")]
    [Arguments("text:level=summary", "Text")]
    [Arguments("parquet", "Parquet")]
    [Arguments("duckdb", "DuckDB")]
    public async Task PacketFormatsResolveToTheirRegistration(string spec, string format)
    {
        using BuiltInRegistry registry = new();

        PacketFormatConfig config = PacketFormatConfig.Parse(spec, registry.Registry);

        await Assert.That(config.Registration.Format).IsEqualTo(format);
    }

    [Test]
    public async Task PacketFormatOptionsKeepTheirTextsAndFlagsBecomeTrue()
    {
        using BuiltInRegistry registry = new();

        PacketFormatConfig pbf = PacketFormatConfig.Parse("pbf:format=columnar,compressed", registry.Registry);
        PacketFormatConfig text = PacketFormatConfig.Parse("text:level=full,truncate=0", registry.Registry);
        PacketFormatConfig noCompress = PacketFormatConfig.Parse("pbf:nocompress", registry.Registry);

        await Assert.That(_OptionText(pbf.OptionTexts)).IsEqualTo("format=columnar,compressed=true");
        await Assert.That(_OptionText(text.OptionTexts)).IsEqualTo("level=full,truncate=0");
        await Assert.That(_OptionText(noCompress.OptionTexts)).IsEqualTo("compressed=false");
    }

    [Test]
    public async Task ParquetIsADirectoryOutputAndDuckDbIsNot()
    {
        using BuiltInRegistry registry = new();

        await Assert.That(PacketFormatConfig.Parse("parquet", registry.Registry).IsDirectoryOutput).IsTrue();
        await Assert.That(PacketFormatConfig.Parse("duckdb", registry.Registry).IsDirectoryOutput).IsFalse();
    }

    [Test]
    [Arguments(".csv", "CSV")]
    [Arguments(".json", "JSON")]
    [Arguments(".JSON", "JSON")]
    [Arguments(".txt", "Text")]
    [Arguments(".pbf", "PBF")]
    [Arguments(".duckdb", "DuckDB")]
    public async Task PacketFormatsAreFoundByTheExtensionOfTheOutput(string extension, string format)
    {
        using BuiltInRegistry registry = new();

        PacketFormatConfig config = PacketFormatConfig.FromExtension(extension, registry.Registry);

        await Assert.That(config.Registration.Format).IsEqualTo(format);
        await Assert.That(config.OptionTexts).IsEmpty();
    }

    [Test]
    public async Task AnUnknownExtensionListsThePacketFormats()
    {
        using BuiltInRegistry registry = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => PacketFormatConfig.FromExtension(".blf", registry.Registry));

        await Assert.That(exception.Message).Contains("Cannot auto-detect export format from extension '.blf'.");
        await Assert.That(exception.Message).Contains("CSV (.csv)");
        await Assert.That(exception.Message).Contains("Parquet (directory; use -f parquet)");
    }

    [Test]
    public async Task AnUnknownOrWrongKindOfPacketFormatIsRefused()
    {
        using BuiltInRegistry registry = new();

        ArgumentException unknown = Assert.Throws<ArgumentException>(() => PacketFormatConfig.Parse("xml", registry.Registry));
        ArgumentException frames = Assert.Throws<ArgumentException>(() => PacketFormatConfig.Parse("blf", registry.Registry));

        await Assert.That(unknown.Message).StartsWith("Unknown format 'xml'.");
        await Assert.That(frames.Message).IsEqualTo("Format 'BLF' writes frames; use 'ni convert'.");
    }

    [Test]
    public async Task PacketOptionProblemsAppearWhenTheListenerIsCreated()
    {
        using BuiltInRegistry registry = new();
        using FrameBuilderStack stack = new();
        string path = Path.Combine(Path.GetTempPath(), "ni-format-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json");
        PacketFormatConfig weird = PacketFormatConfig.Parse("json:style=weird", registry.Registry);
        PacketFormatConfig unknown = PacketFormatConfig.Parse("json:colour=red", registry.Registry);

        bool weirdCreated = weird.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? weirdError, CancellationToken.None);
        bool unknownCreated = unknown.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? unknownError, CancellationToken.None);

        await Assert.That(weirdCreated).IsFalse();
        await Assert.That(weirdError?.Message).StartsWith("style: allowed: compact, pretty, array");
        await Assert.That(unknownCreated).IsFalse();
        await Assert.That(unknownError?.Message).IsEqualTo(
            "Unknown option 'colour'; options: style, shape, fields, occurrence, info, frames, buffers, text, repr, flushPerPacket.");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    /// <summary>
    /// A storage version outside the six is an option problem of the create step: the enum setting names the allowed values and the
    /// DuckDB file is not created. A defined text creates the exporter (the file appears with the first packet).
    /// </summary>
    [Test]
    public async Task AnUnknownDuckDbStorageVersionIsRefusedWithTheAllowedValuesAndNoFile()
    {
        using BuiltInRegistry registry = new();
        using FrameBuilderStack stack = new();
        string path = Path.Combine(Path.GetTempPath(), "ni-format-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".duckdb");
        PacketFormatConfig bad = PacketFormatConfig.Parse("duckdb:storageVersion=v9", registry.Registry);
        PacketFormatConfig good = PacketFormatConfig.Parse("duckdb:storageVersion=v1.2.0", registry.Registry);

        bool badCreated = bad.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? badError, CancellationToken.None);
        bool goodCreated = good.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out IPacketListener? listener, out PluginError? goodError, CancellationToken.None);
        (listener as IDisposable)?.Dispose();

        await Assert.That(badCreated).IsFalse();
        await Assert.That(badError?.Message).IsEqualTo("storageVersion: allowed: v1.0.0, v1.1.0, v1.2.0, v1.3.0, v1.4.0, v1.5.0");
        await Assert.That(goodCreated).IsTrue();
        await Assert.That(goodError).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    /// <summary>
    /// A codec or level outside the registered ones is an option problem of the create step: the enum setting names the allowed
    /// values and no dataset directory is created. Defined values create the exporter (the directory appears with the first packet).
    /// </summary>
    [Test]
    [Arguments("parquet:compression=lzma", "compression: allowed: none, snappy, gzip, brotli, lz4, zstd")]
    [Arguments("parquet:compressionLevel=max", "compressionLevel: allowed: fastest, optimal, smallest")]
    public async Task AnUnknownParquetCodecOrLevelIsRefusedWithTheAllowedValuesAndNoDirectory(string spec, string message)
    {
        using BuiltInRegistry registry = new();
        using FrameBuilderStack stack = new();
        string path = Path.Combine(Path.GetTempPath(), "ni-format-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        PacketFormatConfig bad = PacketFormatConfig.Parse(spec, registry.Registry);
        PacketFormatConfig good = PacketFormatConfig.Parse("parquet:compression=snappy,compressionLevel=fastest", registry.Registry);

        bool badCreated = bad.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? badError, CancellationToken.None);
        bool goodCreated = good.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out IPacketListener? listener, out PluginError? goodError, CancellationToken.None);
        (listener as IDisposable)?.Dispose();

        await Assert.That(badCreated).IsFalse();
        await Assert.That(badError?.Message).IsEqualTo(message);
        await Assert.That(goodCreated).IsTrue();
        await Assert.That(goodError).IsNull();
        await Assert.That(Directory.Exists(path)).IsFalse();
    }

    #endregion

    #region Frame formats

    [Test]
    [Arguments("pcapng", "PCAPNG")]
    [Arguments("pcap", "PCAPNG")]
    [Arguments("blf", "BLF")]
    [Arguments("blf:compression=best", "BLF")]
    [Arguments("asc", "ASC")]
    public async Task FrameFormatsResolveToTheirRegistration(string spec, string format)
    {
        using BuiltInRegistry registry = new();

        FrameFormatConfig config = FrameFormatConfig.Parse(spec, registry.Registry);

        await Assert.That(config.Registration.Format).IsEqualTo(format);
    }

    [Test]
    [Arguments("off", "none")]
    [Arguments("no", "none")]
    [Arguments("normal", "default")]
    [Arguments("high", "best")]
    [Arguments("max", "best")]
    [Arguments("fast", "fast")]
    public async Task LegacyBlfCompressionNamesMapToTheNamesOfTheOption(string legacy, string expected)
    {
        using BuiltInRegistry registry = new();

        FrameFormatConfig config = FrameFormatConfig.Parse("blf:compression=" + legacy, registry.Registry);

        await Assert.That(_OptionText(config.OptionTexts)).IsEqualTo("compression=" + expected);
    }

    [Test]
    public async Task AnUnknownCompressionIsAnOptionProblemOfTheCreateStep()
    {
        using BuiltInRegistry registry = new();
        using FrameBuilderStack stack = new();
        FrameFormatConfig config = FrameFormatConfig.Parse("blf:compression=ultra", registry.Registry);
        string path = Path.Combine(Path.GetTempPath(), "ni-format-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".blf");

        bool created = config.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? error, CancellationToken.None);

        await Assert.That(created).IsFalse();
        await Assert.That(error?.Message).IsEqualTo("compression: allowed: none, fast, default, best");
    }

    /// <summary>
    /// The additional buffers are packet content, so a frame format (which writes frames and nothing else) has no such option: the
    /// option is refused as an unknown option with the list of the options the format does have, and no file is created.
    /// </summary>
    [Test]
    [Arguments("pcapng:buffers=true", ".pcapng")]
    [Arguments("pcap:buffers=true", ".pcapng")]
    [Arguments("blf:buffers=true", ".blf")]
    [Arguments("asc:buffers=true", ".asc")]
    public async Task TheBuffersOptionIsAnUnknownOptionOfEveryFrameFormat(string spec, string extension)
    {
        using BuiltInRegistry registry = new();
        using FrameBuilderStack stack = new();
        FrameFormatConfig config = FrameFormatConfig.Parse(spec, registry.Registry);
        string path = Path.Combine(Path.GetTempPath(), "ni-format-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + extension);

        bool created = config.TryCreate(path, stack.Stack, stack.Stack.Settings.GetLimit, static _ => { },
            out _, out PluginError? error, CancellationToken.None);

        await Assert.That(created).IsFalse();
        await Assert.That(error?.Message).StartsWith("Unknown option 'buffers'; options: ");
        await Assert.That(error!.Value.Message.Contains("frames", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [Arguments(".pcapng", "PCAPNG")]
    [Arguments(".pcap", "PCAPNG")]
    [Arguments(".blf", "BLF")]
    [Arguments(".asc", "ASC")]
    [Arguments("", "PCAPNG")]
    [Arguments(".out", "PCAPNG")]
    public async Task FrameFormatsAreFoundByExtensionAndAnythingElseIsPcapng(string extension, string format)
    {
        using BuiltInRegistry registry = new();

        FrameFormatConfig config = FrameFormatConfig.FromExtension(extension, registry.Registry);

        await Assert.That(config.Registration.Format).IsEqualTo(format);
    }

    [Test]
    public async Task AnUnknownOrWrongKindOfFrameFormatIsRefused()
    {
        using BuiltInRegistry registry = new();

        ArgumentException unknown = Assert.Throws<ArgumentException>(() => FrameFormatConfig.Parse("xml", registry.Registry));
        ArgumentException packets = Assert.Throws<ArgumentException>(() => FrameFormatConfig.Parse("csv", registry.Registry));

        await Assert.That(unknown.Message).StartsWith("Unknown format 'xml'.");
        await Assert.That(packets.Message).IsEqualTo("Format 'CSV' writes packets; use 'ni export'.");
    }

    #endregion
}

/// <summary>A standard protocol stack for tests that need an <see cref="IStack"/> for an export request.</summary>
internal sealed class FrameBuilderStack : IDisposable
{
    /// <summary>Builds the stack.</summary>
    internal FrameBuilderStack()
    {
        SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        Stack = builder.Build();
    }

    /// <summary>The stack; disposing this object disposes it.</summary>
    internal Stack Stack { get; }

    /// <summary>Disposes the stack and its settings.</summary>
    public void Dispose() => Stack.Dispose();
}
