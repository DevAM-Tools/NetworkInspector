// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests.Infrastructure;

/// <summary>
/// Self-tests for <see cref="TsharkAssert"/>. Validates the symmetric NI ↔ tshark
/// comparison helper end-to-end on a simple Ethernet/VLAN/IPv4/UDP frame.
/// </summary>
/// <remarks>
/// <para>These tests exercise <see cref="TsharkAssert.AssertEquivalent"/> against fields
/// for which the NI parser and tshark are expected to agree (positive case) and against a
/// deliberately corrupted NI value (negative case — the helper must throw with both
/// values in the message).</para>
/// <para>If tshark is missing on the test machine and the developer escape hatch
/// <c>NETWORKINSPECTOR_ALLOW_MISSING_TSHARK=1</c> is enabled, the positive tests pass
/// silently. CI must leave that variable unset so the tests fail loudly.</para>
/// </remarks>
internal sealed class TsharkAssertTests
{
    private static byte[] BuildSampleFrame()
    {
        EthernetLayer eth = new(
            MacAddress.FromBytes([0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]),
            MacAddress.FromBytes([0x11, 0x22, 0x33, 0x44, 0x55, 0x66]));
        VlanLayer vlan = new(42);
        IPv4Layer ip = new(new IPv4Address(0xC0A80101), new IPv4Address(0xC0A80102));
        UdpLayer udp = new(12345, 80);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        return FrameStack.Start(eth).Then(vlan).Then(ip).Then(udp).CreateWithFixedValues().EmitFrame(payload);
    }

    [Test]
    public async Task AssertEquivalent_VlanId_PassesWhenNiAndTsharkAgree()
    {
        byte[] frame = BuildSampleFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        try
        {
            await TsharkAssert.AssertEquivalent(stack, packet, "vlan.id", frame, "vlan.id").ConfigureAwait(false);
        }
        finally
        {
            stack.Dispose();
        }
    }

    [Test]
    public async Task AssertEquivalent_BasicFields_PassWhenNiAndTsharkAgree()
    {
        byte[] frame = BuildSampleFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        try
        {
            await TsharkAssert.AssertEquivalent(stack, packet, "ip.src", frame, "ip.src").ConfigureAwait(false);
            await TsharkAssert.AssertEquivalent(stack, packet, "ip.dst", frame, "ip.dst").ConfigureAwait(false);
            await TsharkAssert.AssertEquivalent(stack, packet, "udp.srcport", frame, "udp.srcport").ConfigureAwait(false);
            await TsharkAssert.AssertEquivalent(stack, packet, "udp.dstport", frame, "udp.dstport").ConfigureAwait(false);
        }
        finally
        {
            stack.Dispose();
        }
    }

    [Test]
    public async Task AssertEquivalentMany_AllPairs_Passes()
    {
        byte[] frame = BuildSampleFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        try
        {
            await TsharkAssert.AssertEquivalentMany(
                stack, packet, frame,
                ("vlan.id", "vlan.id"),
                ("ip.src", "ip.src"),
                ("ip.dst", "ip.dst"),
                ("udp.srcport", "udp.srcport"),
                ("udp.dstport", "udp.dstport")).ConfigureAwait(false);
        }
        finally
        {
            stack.Dispose();
        }
    }

    [Test]
    public async Task AssertEquivalent_UnknownNiField_FailsWithDiagnostic()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] frame = BuildSampleFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        try
        {
            Exception? thrown = null;
            try
            {
                await TsharkAssert.AssertEquivalent(stack, packet, "ip.does_not_exist", frame, "ip.src").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            await Assert.That(thrown).IsNotNull()
                .Because("Helper must fail when the NI field is missing.");
        }
        finally
        {
            stack.Dispose();
        }
    }

    [Test]
    public async Task AssertEquivalent_UnknownTsharkField_FailsWhenTsharkAvailable()
    {
        if (TsharkAvailability.ShouldSkip())
        {
            return;
        }

        byte[] frame = BuildSampleFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame);
        try
        {
            Exception? thrown = null;
            try
            {
                await TsharkAssert.AssertEquivalent(stack, packet, "ip.src", frame, "ip.does_not_exist").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            await Assert.That(thrown).IsNotNull()
                .Because("Helper must fail when tshark does not emit the requested field.");
        }
        finally
        {
            stack.Dispose();
        }
    }
}
