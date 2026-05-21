// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Unit tests for <see cref="FlexRayFlagsFormatter"/> covering all indicator flag combinations
/// (16 entries) and key error flag combinations from the 32-entry table.
/// </summary>
internal sealed class FlexRayFlagsFormatterTests
{
    #region Indicator flags (4 flags: PPI, NFI, SFI, STFI — 16 entries)

    [Test]
    public async Task FormatIndicators_NoFlags() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(false, false, false, false)).IsEqualTo("[None]");

    [Test]
    public async Task FormatIndicators_PpiOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(true, false, false, false)).IsEqualTo("[PPI]");

    [Test]
    public async Task FormatIndicators_NfiOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(false, true, false, false)).IsEqualTo("[NFI]");

    [Test]
    public async Task FormatIndicators_SfiOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(false, false, true, false)).IsEqualTo("[SFI]");

    [Test]
    public async Task FormatIndicators_StfiOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(false, false, false, true)).IsEqualTo("[STFI]");

    [Test]
    public async Task FormatIndicators_NfiSfi() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(false, true, true, false)).IsEqualTo("[NFI, SFI]");

    [Test]
    public async Task FormatIndicators_AllSet() =>
        await Assert.That(FlexRayFlagsFormatter.FormatIndicators(true, true, true, true)).IsEqualTo("[PPI, NFI, SFI, STFI]");

    #endregion

    #region Error flags (5 flags: FCRC_ERR, HCRC_ERR, FES_ERR, COD_ERR, TSS_VIOL — 32 entries)

    [Test]
    public async Task FormatErrors_NoFlags() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(false, false, false, false, false)).IsEqualTo("[None]");

    [Test]
    public async Task FormatErrors_FcrcErrOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(true, false, false, false, false)).IsEqualTo("[FCRC_ERR]");

    [Test]
    public async Task FormatErrors_HcrcErrOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(false, true, false, false, false)).IsEqualTo("[HCRC_ERR]");

    [Test]
    public async Task FormatErrors_FesErrOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(false, false, true, false, false)).IsEqualTo("[FES_ERR]");

    [Test]
    public async Task FormatErrors_CodErrOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(false, false, false, true, false)).IsEqualTo("[COD_ERR]");

    [Test]
    public async Task FormatErrors_TssViolOnly() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(false, false, false, false, true)).IsEqualTo("[TSS_VIOL]");

    [Test]
    public async Task FormatErrors_FcrcAndHcrc() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(true, true, false, false, false)).IsEqualTo("[FCRC_ERR, HCRC_ERR]");

    [Test]
    public async Task FormatErrors_AllSet() =>
        await Assert.That(FlexRayFlagsFormatter.FormatErrors(true, true, true, true, true)).IsEqualTo("[FCRC_ERR, HCRC_ERR, FES_ERR, COD_ERR, TSS_VIOL]");

    #endregion
}
