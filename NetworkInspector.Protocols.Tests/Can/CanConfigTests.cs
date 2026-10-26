// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the CAN message name configuration: ids that appear more than once are reported, the first entry is kept.
/// </summary>
internal sealed class CanConfigTests
{
    /// <summary>Registers a CAN protocol with the given configuration text and returns it.</summary>
    private static async Task<CanProtocol> _RegisterAsync(string directory, string json)
    {
        string configPath = Path.Combine(directory, "can.json");
        await File.WriteAllTextAsync(configPath, json).ConfigureAwait(false);
        SettingsManager settings = new(directory);
        settings.PreloadValue("can.config_file", "can.json");
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        FrameProtocol frame = new();
        frame.RegisterFields(builder, builder.RegisterProtocol(frame));
        CanProtocol can = new();
        can.RegisterFields(builder, builder.RegisterProtocol(can));
        settings.Dispose();
        return can;
    }

    private static string _NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        return directory;
    }

    [Test]
    public async Task RepeatedMessageIdIsReportedAsAWarning()
    {
        string directory = _NewDirectory();
        try
        {
            CanProtocol can = await _RegisterAsync(
                directory, """{"messages":[{"id": 291, "name": "A"}, {"id": 291, "name": "B"}]}""").ConfigureAwait(false);

            await Assert.That(can.ConfigLoadWarning).IsNotNull();
            await Assert.That(can.ConfigLoadWarning!.Value.Kind).IsEqualTo(SettingsLoadWarningKind.DeserializationError);
            await Assert.That(can.ConfigLoadWarning!.Value.SettingName).IsEqualTo("can.config_file");
            await Assert.That(can.ConfigLoadWarning!.Value.Message).Contains("0x123");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task StandardAndExtendedEntryWithTheSameNumberAreReportedAsRepeated()
    {
        // The lookup has one id space for standard and extended frames, so these two entries collide.
        string directory = _NewDirectory();
        try
        {
            CanProtocol can = await _RegisterAsync(
                directory,
                """{"messages":[{"id": 291, "name": "Std"}, {"id": 291, "name": "Ext", "extended": true}]}""").ConfigureAwait(false);

            await Assert.That(can.ConfigLoadWarning).IsNotNull();
            await Assert.That(can.ConfigLoadWarning!.Value.Message).Contains("0x123");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task DistinctMessageIdsProduceNoWarning()
    {
        string directory = _NewDirectory();
        try
        {
            CanProtocol can = await _RegisterAsync(
                directory, """{"messages":[{"id": 291, "name": "A"}, {"id": 292, "name": "B"}]}""").ConfigureAwait(false);

            await Assert.That(can.ConfigLoadWarning).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task NullEntryAndRepeatedIdAreBothReported()
    {
        string directory = _NewDirectory();
        try
        {
            CanProtocol can = await _RegisterAsync(
                directory,
                """{"messages":[null, {"id": 291, "name": "A"}, {"id": 291, "name": "B"}]}""").ConfigureAwait(false);

            await Assert.That(can.ConfigLoadWarning).IsNotNull();
            await Assert.That(can.ConfigLoadWarning!.Value.Message).Contains("null");
            await Assert.That(can.ConfigLoadWarning!.Value.Message).Contains("0x123");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task FirstEntryOfARepeatedIdKeepsItsName()
    {
        string directory = _NewDirectory();
        try
        {
            string configPath = Path.Combine(directory, "can.json");
            await File.WriteAllTextAsync(configPath, """{"messages":[{"id": 291, "name": "First"}, {"id": 291, "name": "Second"}]}""")
                .ConfigureAwait(false);
            byte[] frame = FrameStack.Start(new SocketCanLayer(0x123u, [1, 2])).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);

            (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(
                frame,
                settings =>
                {
                    settings.PreloadValue("can.config_file", configPath);
                },
                LinkType.CanSocketcan);
            using (stack)
            {
                await ProtocolTestHelper.AssertStringField(stack, packet, "can.name", "First").ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
