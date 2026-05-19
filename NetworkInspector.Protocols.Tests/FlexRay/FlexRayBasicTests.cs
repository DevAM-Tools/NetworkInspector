// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Happy-path integration tests for the FlexRay protocol parser.
/// Frames are built with <see cref="FlexRayLayer"/> so the tests exercise the same
/// code path used by user-facing frame builders.
/// </summary>
/// <remarks>Thread safety: stateless tests; no shared mutable state.</remarks>
internal sealed class FlexRayBasicTests
{
    private static byte[] BuildFrame(
        ushort frameId = 1,
        byte cycleCount = 0,
        bool nfi = false,
        bool sfi = false,
        bool stfi = false,
        bool ppi = false,
        byte errorFlags = 0)
    {
        FlexRayLayer fr = new(
            frameId,
            cycleCount,
            payload: new byte[4],
            nfi: nfi,
            sfi: sfi,
            stfi: stfi,
            ppi: ppi,
            errorFlags: errorFlags);
        return FrameStack.Start(fr).CreateWithFixedValues().EmitFrame(ReadOnlySpan<byte>.Empty);
    }

    #region Indicator flags container display text

    [Test]
    public async Task Parse_FlexRay_IndicatorFlags_DisplayText_None()
    {
        byte[] frame = BuildFrame();
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.flags", "[None]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FlexRay_IndicatorFlags_DisplayText_Nfi()
    {
        byte[] frame = BuildFrame(nfi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.flags", "[NFI]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FlexRay_IndicatorFlags_DisplayText_SfiAndStfi()
    {
        byte[] frame = BuildFrame(sfi: true, stfi: true);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.flags", "[SFI, STFI]").ConfigureAwait(false);
        }
    }

    #endregion

    #region Error flags container display text

    [Test]
    public async Task Parse_FlexRay_ErrorFlags_DisplayText_None()
    {
        byte[] frame = BuildFrame(errorFlags: 0x00);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.err_flags", "[None]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlags_DisplayText_FcrcErr()
    {
        // FCRC_ERR is at wire bit 4 of the error flags byte (mask 0x10).
        byte[] frame = BuildFrame(errorFlags: 0x10);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.err_flags", "[FCRC_ERR]").ConfigureAwait(false);
        }
    }

    [Test]
    public async Task Parse_FlexRay_ErrorFlags_DisplayText_HcrcAndTssViol()
    {
        // HCRC_ERR is wire bit 3 (mask 0x08); TSS_VIOL is wire bit 0 (mask 0x01).
        byte[] frame = BuildFrame(errorFlags: 0x09);
        (Stack stack, Packet packet) = ProtocolTestHelper.BuildAndParse(frame, LinkType.Flexray);
        using (stack)
        {
            await ProtocolTestHelper.AssertDisplayText(stack, packet, "flexray.err_flags", "[HCRC_ERR, TSS_VIOL]").ConfigureAwait(false);
        }
    }

    #endregion
}
