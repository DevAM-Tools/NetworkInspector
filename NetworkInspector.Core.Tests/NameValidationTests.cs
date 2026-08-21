// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Exit-point coverage for <see cref="NameValidation"/> helpers.
/// </summary>
internal sealed class NameValidationTests
{
    [Test]
    public async Task IsValidName_RejectsEmptyAndTrailingDot()
    {
        await Assert.That(NameValidation.IsValidName(ReadOnlySpan<char>.Empty)).IsFalse();
        await Assert.That(NameValidation.IsValidName("eth.".AsSpan())).IsFalse();
        await Assert.That(NameValidation.IsValidName("1bad".AsSpan())).IsFalse();
    }

    [Test]
    public async Task IsValidName_AcceptsDottedIdentifiers()
    {
        await Assert.That(NameValidation.IsValidName("eth.type".AsSpan())).IsTrue();
        await Assert.That(NameValidation.IsValidName("_private".AsSpan())).IsTrue();
        await Assert.That(NameValidation.IsValidName("ip.src".AsSpan())).IsTrue();
    }

    [Test]
    [Arguments("ip.srç")]
    [Arguments("äther")]
    [Arguments("ip.Ω")]
    [Arguments("f１")]
    public async Task IsValidName_NonAsciiLetterOrDigit_ReturnsFalse(string name)
    {
        await Assert.That(NameValidation.IsValidName(name.AsSpan())).IsFalse();
    }

    [Test]
    public async Task IsValidGroupName_RejectsInvalidOrUppercaseNames()
    {
        await Assert.That(NameValidation.IsValidGroupName(ReadOnlySpan<char>.Empty)).IsTrue();
        await Assert.That(NameValidation.IsValidGroupName("BadGroup".AsSpan())).IsFalse();
        await Assert.That(NameValidation.IsValidGroupName("bad.name".AsSpan())).IsTrue();
        await Assert.That(NameValidation.IsValidGroupName("".AsSpan())).IsTrue();
        await Assert.That(NameValidation.IsValidGroupName("not-valid".AsSpan())).IsFalse();
    }

    [Test]
    public async Task IsValidUiName_RejectsEmptyAndControlCharacters()
    {
        await Assert.That(NameValidation.IsValidUiName(ReadOnlySpan<char>.Empty)).IsFalse();
        await Assert.That(NameValidation.IsValidUiName("ok".AsSpan())).IsTrue();
        await Assert.That(NameValidation.IsValidUiName("bad\nline".AsSpan())).IsFalse();
    }
}
