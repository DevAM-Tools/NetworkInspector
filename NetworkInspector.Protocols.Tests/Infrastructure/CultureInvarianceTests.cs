// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Display text never depends on the thread culture: the same frames parsed and exported on a <c>de-DE</c> thread and on an
/// invariant thread give identical packet info, custom texts and Text export (decimal point, no thousands separators).
/// </summary>
internal sealed class CultureInvarianceTests
{
    #region Helpers

    private static readonly MacAddress _DstMac = MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
    private static readonly MacAddress _SrcMac = MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]);

    /// <summary>Parses the frame and returns the packet info followed by the full Text export of the packet.</summary>
    private static string _Render(byte[] frame, LinkType linkType, Action<SettingsManager>? configure)
    {
        (Stack stack, Packet packet) = configure is null
            ? ProtocolTestHelper.BuildAndParse(frame, linkType)
            : ProtocolTestHelper.BuildAndParse(frame, configure, linkType);
        using (stack)
        using (MemoryStream stream = new())
        {
            using (TextExporter exporter = TextExporter.CreateBuilder()
                .ToStream(stream)
                .WithDetailLevel(TextDetailLevel.Full)
                .WithMaxTextLength(0)
                .Build())
            {
                _ = exporter.OnPacket(packet);
                exporter.OnFinish();
            }

            return packet.Info + "\n" + Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>
    /// Renders the same frame once per culture, each on its own thread-pool task. The culture is set inside the task; it
    /// flows with the task's execution context only, so it cannot leak into other tests that reuse the pool thread.
    /// </summary>
    private static async Task<(string German, string Invariant)> _RenderBoth(
        byte[] frame, LinkType linkType, Action<SettingsManager>? configure = null)
    {
        CultureInfo german = CultureInfo.GetCultureInfo("de-DE");
        string onGerman = await Task.Run(() =>
        {
            CultureInfo.CurrentCulture = german;
            CultureInfo.CurrentUICulture = german;
            return _Render(frame, linkType, configure);
        }).ConfigureAwait(false);
        string onInvariant = await Task.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return _Render(frame, linkType, configure);
        }).ConfigureAwait(false);
        return (onGerman, onInvariant);
    }

    #endregion

    [Test]
    public async Task UdpOverIpv6TextIsCultureIndependent()
    {
        IPv6Address src = IPv6Address.FromBytes(Convert.FromHexString("20010DB8000000000000000000000001"));
        IPv6Address dst = IPv6Address.FromBytes(Convert.FromHexString("20010DB8000000000000000000000002"));
        byte[] frame = FrameStack
            .Start(new EthernetLayer(_DstMac, _SrcMac))
            .Then(new IPv6Layer(src, dst))
            .Then(new UdpLayer(srcPort: 12345, dstPort: 30490))
            .CreateWithFixedValues()
            .EmitFrame([1, 2, 3, 4]);

        (string german, string invariant) = await _RenderBoth(frame, LinkType.Ethernet).ConfigureAwait(false);

        await Assert.That(invariant.Contains("12345", StringComparison.Ordinal)).IsTrue();
        await Assert.That(invariant.Contains("12.345", StringComparison.Ordinal)).IsFalse();
        await Assert.That(german).IsEqualTo(invariant);
    }

    [Test]
    public async Task ClassicCanTextIsCultureIndependent()
    {
        byte[] frame = FrameStack
            .Start(new SocketCanLayer(0x123u, [0xDE, 0xAD, 0xBE, 0xEF]))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);

        (string german, string invariant) = await _RenderBoth(frame, LinkType.CanSocketcan).ConfigureAwait(false);

        await Assert.That(invariant.Contains("0x00000123", StringComparison.Ordinal)).IsTrue();
        await Assert.That(german).IsEqualTo(invariant);
    }

    [Test]
    public async Task SomeIpTextIsCultureIndependent()
    {
        byte[] frame = FrameStack
            .Start(new EthernetLayer(_DstMac, _SrcMac))
            .Then(new IPv4Layer(IPv4Address.FromBytes([192, 168, 1, 1]), IPv4Address.FromBytes([192, 168, 1, 2])))
            .Then(new UdpLayer(srcPort: 12345, dstPort: 30490))
            .Then(new SomeIpLayer(
                serviceId: 0x1234, methodId: 0x0042, clientId: 0x0001, sessionId: 0x0010, messageType: SomeIpMessageType.Request))
            .CreateWithFixedValues()
            .EmitFrame([0xDE, 0xAD, 0xBE, 0xEF]);

        (string german, string invariant) = await _RenderBoth(frame, LinkType.Ethernet).ConfigureAwait(false);

        await Assert.That(german).IsEqualTo(invariant);
    }

    [Test]
    public async Task SignalMessagePhysicalValueUsesADecimalPointOnAGermanThread()
    {
        SignalMessageLayout layout = new()
        {
            PduId = 0x100,
            Name = "can_signal",
            UiName = "CAN Signal",
            ByteLength = 4,
            Signals = AutomotivePduBench.TwoSequentialUint16LeLayout.Signals,
            DispatchBindings = ImmutableArray.Create(
                new FrameDispatchBinding { Table = CanProtocol.IdTableName, Key = 0x100 }),
            Mux = null,
            MuxGroups = [],
        };
        SignalMessageValueSet values = SignalMessageValueSet.For(layout).Set("EngineRpm", 125.0).Set("Thr", 555);
        byte[] signalBytes = new byte[layout.ByteLength];
        new SignalMessageLayer(layout, values).WriteHeader(signalBytes.AsSpan());
        byte[] frame = FrameStack
            .Start(new SocketCanLayer(0x100u, signalBytes))
            .CreateWithFixedValues()
            .EmitFrame(ReadOnlySpan<byte>.Empty);
        string directory = Path.Combine(Path.GetTempPath(), "ni_culture_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            string jsonPath = Path.Combine(directory, "signal_message.json");
            await File.WriteAllTextAsync(jsonPath, SignalMessageConfigBridge.SerializeJson(layout)).ConfigureAwait(false);

            (string german, string invariant) = await _RenderBoth(
                frame, LinkType.CanSocketcan, sm => sm.PreloadValue("signal_message.config_file", jsonPath)).ConfigureAwait(false);

            await Assert.That(invariant.Contains("125.00", StringComparison.Ordinal)).IsTrue();
            await Assert.That(german).IsEqualTo(invariant);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
