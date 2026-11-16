// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Tests for <see cref="ExporterCreation{TListener}"/>: a registration holds exactly one way to create (a target of streams or a
/// prepared file path), the loader refuses a registration that breaks that rule, and the contract assembly shows only the new types.
/// </summary>
internal sealed class ExporterCreationTests
{
    #region Helpers

    private static bool _NoStreamExporter(StreamExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = null;
        error = new PluginError(PluginErrorCode.Failed, "No exporter.");
        return false;
    }

    private static bool _NoFileExporter(FileExportRequest request, [NotNullWhen(true)] out IPacketListener? listener,
        [NotNullWhen(false)] out PluginError? error)
    {
        listener = null;
        error = new PluginError(PluginErrorCode.Failed, "No exporter.");
        return false;
    }

    #endregion

    #region Kinds

    [Test]
    public async Task AStreamCreationHoldsOnlyTheStreamDelegate()
    {
        ExporterCreation<IPacketListener> creation = ExporterCreation.ForStreams<IPacketListener>(_NoStreamExporter);

        await Assert.That(creation.WritesFile).IsFalse();
        await Assert.That(creation.TryGetForStreams(out StreamExporterCreate<IPacketListener>? streams)).IsTrue();
        await Assert.That(streams).IsNotNull();
        await Assert.That(creation.TryGetForFile(out FileExporterCreate<IPacketListener>? file)).IsFalse();
        await Assert.That(file).IsNull();
    }

    [Test]
    public async Task AFileCreationHoldsOnlyTheFileDelegate()
    {
        ExporterCreation<IPacketListener> creation = ExporterCreation.ForFile<IPacketListener>(_NoFileExporter);

        await Assert.That(creation.WritesFile).IsTrue();
        await Assert.That(creation.TryGetForFile(out FileExporterCreate<IPacketListener>? file)).IsTrue();
        await Assert.That(file).IsNotNull();
        await Assert.That(creation.TryGetForStreams(out StreamExporterCreate<IPacketListener>? streams)).IsFalse();
        await Assert.That(streams).IsNull();
    }

    [Test]
    public async Task BothFactoriesRefuseANullDelegate()
    {
        await Assert.That(static () => ExporterCreation.ForStreams<IPacketListener>(null!)).Throws<ArgumentNullException>();
        await Assert.That(static () => ExporterCreation.ForFile<IPacketListener>(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TheCreationTypeHasNoPublicConstructor()
    {
        int publicConstructors = typeof(ExporterCreation<IPacketListener>).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length;

        await Assert.That(publicConstructors).IsEqualTo(0);
    }

    #endregion

    #region Loader rules

    [Test]
    public async Task AFileRegistrationWithoutAnExtensionFailsThePluginAndNamesTheFormat()
    {
        using LoaderHarness harness = new("creation-file-no-extension");
        ExporterRegistration<IPacketListener> fileWithoutExtension = ScriptedPlugin.PacketExporter("DataFmt") with
        {
            FileExtension = string.Empty,
            Creation = ExporterCreation.ForFile<IPacketListener>(_NoFileExporter),
        };

        PluginLoadReport report = harness.Load([], [new ScriptedPlugin("P1", b => b.AddPacketExporter(fileWithoutExtension))]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Failed);
        await Assert.That(report.Plugins[0].Reason).Contains("DataFmt");
        await Assert.That(report.Plugins[0].Reason).Contains("needs a file extension");
    }

    [Test]
    public async Task AStreamRegistrationWithoutAnExtensionIsADatasetFormatAndLoads()
    {
        using LoaderHarness harness = new("creation-stream-no-extension");
        ExporterRegistration<IPacketListener> dataset = ScriptedPlugin.PacketExporter("DataFmt") with { FileExtension = string.Empty };

        PluginLoadReport report = harness.Load([], [new ScriptedPlugin("P1", b => b.AddPacketExporter(dataset))]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Loaded);
    }

    [Test]
    public async Task ARegistrationWithoutACreationFailsThePlugin()
    {
        using LoaderHarness harness = new("creation-null");
        // Deliberate null: the builder must refuse it.
        ExporterRegistration<IPacketListener> noCreation = ScriptedPlugin.PacketExporter("DataFmt") with { Creation = null! };

        PluginLoadReport report = harness.Load([], [new ScriptedPlugin("P1", b => b.AddPacketExporter(noCreation))]);

        await Assert.That(report.Plugins[0].State).IsEqualTo(PluginLoadState.Failed);
    }

    #endregion
}
