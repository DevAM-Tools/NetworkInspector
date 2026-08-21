// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for non-generic <see cref="ParseResult"/>.</summary>
internal sealed class ParseResultTests
{
    [Test]
    public async Task NotDispatched_IsSuccessFalse_IsErrorFalse_IsNotDispatchedTrue()
    {
        ParseResult result = ParseResult.NotDispatched;

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.IsNotDispatched).IsTrue();
    }

    [Test]
    public async Task NotDispatched_TryGetError_ReturnsFalse()
    {
        ParseResult result = ParseResult.NotDispatched;

        bool hasError = result.TryGetError(out ParseError error);

        await Assert.That(hasError).IsFalse();
        await Assert.That(error).IsEqualTo(default(ParseError));
    }

    [Test]
    public async Task NotDispatched_ToString_IsNotDispatched()
    {
        ParseResult result = ParseResult.NotDispatched;
        string text = result.ToString();

        await Assert.That(text).IsEqualTo("NotDispatched");
    }

    [Test]
    public async Task OkZero_IsSuccessTrue_ConsumedIsZero()
    {
        ParseResult result = 0;

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.TryGetConsumed(out int consumed)).IsTrue();
        await Assert.That(consumed).IsEqualTo(0);
        await Assert.That(result.IsNotDispatched).IsFalse();
        await Assert.That(result.IsError).IsFalse();
    }

    [Test]
    public async Task ProtocolTableMissing_Implicit_IsError_KindMatches()
    {
        ParseResult result = ParseError.ProtocolTableMissing("udp", "missing");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.IsNotDispatched).IsFalse();
        await Assert.That(result.TryGetError(out ParseError error)).IsTrue();
        await Assert.That(error.Kind).IsEqualTo(ParseErrorKind.ProtocolTableMissing);
        await Assert.That(error.ProtocolName).IsEqualTo("udp");
        await Assert.That(error.Message).IsEqualTo("missing");
    }

    [Test]
    public async Task ImplicitFromParseError_NeverSetsIsNotDispatched()
    {
        ParseResult result = ParseError.InsufficientData("eth");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.IsNotDispatched).IsFalse();
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Success_ToString_FormatsConsumedBytes()
    {
        ParseResult result = 7;
        await Assert.That(result.ToString()).IsEqualTo("Ok(7)");
    }

    [Test]
    public async Task Error_ToString_FormatsErrorMessage()
    {
        ParseResult result = ParseError.Custom("tcp", "bad checksum");
        await Assert.That(result.ToString()).IsEqualTo("Error(bad checksum)");
    }

    [Test]
    public async Task ImplicitFromNegativeConsumed_ThrowsArgumentOutOfRangeException()
    {
        await Assert
            .That(() =>
            {
                ParseResult result = -1;
                _ = result;
            })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ImplicitFromIntMaxValue_ThrowsArgumentOutOfRangeException()
    {
        await Assert
            .That(() =>
            {
                ParseResult result = int.MaxValue;
                _ = result;
            })
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ImplicitFromIntMaxValueMinusOne_RoundTripsConsumed()
    {
        ParseResult result = int.MaxValue - 1;

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.TryGetConsumed(out int consumed)).IsTrue();
        await Assert.That(consumed).IsEqualTo(int.MaxValue - 1);
    }

    [Test]
    public async Task Default_IsError_WithUninitializedMessage()
    {
        ParseResult result = default;

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.TryGetError(out ParseError error)).IsTrue();
        await Assert.That(error.Message).Contains("Uninitialized ParseResult");
    }

    [Test]
    public async Task TryPropagateError_OkZero_ReturnsFalse()
    {
        ParseResult result = 0;

        await Assert.That(result.TryPropagateError(out _)).IsFalse();
    }

    [Test]
    public async Task TryPropagateError_OkN_ReturnsFalse()
    {
        ParseResult result = 7;

        await Assert.That(result.TryPropagateError(out _)).IsFalse();
    }

    [Test]
    public async Task TryPropagateError_NotDispatched_ReturnsFalse()
    {
        await Assert.That(ParseResult.NotDispatched.TryPropagateError(out _)).IsFalse();
    }

    [Test]
    public async Task TryPropagateError_ErrorVariant_ReturnsTrueAndSelf()
    {
        ParseResult result = ParseError.InternalError("boom");

        await Assert.That(result.TryPropagateError(out ParseResult propagate)).IsTrue();
        await Assert.That(propagate.TryGetError(out ParseError error)).IsTrue();
        await Assert.That(error.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task TryPropagateError_Default_ReturnsTrueAndUninitializedMessage()
    {
        ParseResult result = default;

        await Assert.That(result.TryPropagateError(out ParseResult propagate)).IsTrue();
        await Assert.That(propagate.TryGetError(out ParseError error)).IsTrue();
        await Assert.That(error.Message).Contains("Uninitialized ParseResult");
    }

    [Test]
    public async Task TryGetConsumed_OkZero_ReturnsTrueAndZero()
    {
        ParseResult result = 0;

        await Assert.That(result.TryGetConsumed(out int consumed)).IsTrue();
        await Assert.That(consumed).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetConsumed_OkN_ReturnsTrueAndN()
    {
        ParseResult result = 42;

        await Assert.That(result.TryGetConsumed(out int consumed)).IsTrue();
        await Assert.That(consumed).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetConsumed_NotOk_ConsumedIsZero()
    {
        _ = ParseResult.NotDispatched.TryGetConsumed(out int missConsumed);
        ParseResult errorResult = ParseError.InternalError("x");
        _ = errorResult.TryGetConsumed(out int errorConsumed);
        _ = default(ParseResult).TryGetConsumed(out int defaultConsumed);

        await Assert.That(missConsumed).IsEqualTo(0);
        await Assert.That(errorConsumed).IsEqualTo(0);
        await Assert.That(defaultConsumed).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetConsumed_NotDispatched_ReturnsFalse()
    {
        await Assert.That(ParseResult.NotDispatched.TryGetConsumed(out _)).IsFalse();
    }

    [Test]
    public async Task TryGetConsumed_Error_ReturnsFalse()
    {
        ParseResult result = ParseError.InternalError("x");

        await Assert.That(result.TryGetConsumed(out _)).IsFalse();
    }

    [Test]
    public async Task Contract_AfterNoError_FalseConsumed_IsNotDispatched()
    {
        ParseResult miss = ParseResult.NotDispatched;
        await Assert.That(miss.TryPropagateError(out _)).IsFalse();
        await Assert.That(miss.TryGetConsumed(out _)).IsFalse();
        await Assert.That(miss.IsNotDispatched).IsTrue();
    }

    [Test]
    public async Task Contract_AfterNoError_TrueConsumedZero_IsOkZeroNotMiss()
    {
        ParseResult okZero = 0;
        await Assert.That(okZero.TryPropagateError(out _)).IsFalse();
        await Assert.That(okZero.TryGetConsumed(out int consumed)).IsTrue();
        await Assert.That(consumed).IsEqualTo(0);
        await Assert.That(okZero.IsNotDispatched).IsFalse();
    }
}
