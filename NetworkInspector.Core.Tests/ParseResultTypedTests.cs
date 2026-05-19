// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ParseResult{T}"/>: success path, failure path, implicit
/// conversions, TryGet helpers, and ToString formatting.
/// These tests document and lock down the contract of the generic discriminated result type.
/// </summary>
internal sealed class ParseResultTypedTests
{
    // === Success path ===

    [Test]
    public async Task Ok_IsSuccess_IsTrue()
    {
        ParseResult<int> result = ParseResult<int>.Ok(42);
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Ok_IsError_IsFalse()
    {
        ParseResult<int> result = ParseResult<int>.Ok(42);
        await Assert.That(result.IsError).IsFalse();
    }

    [Test]
    public async Task Ok_Value_ReturnsStoredValue()
    {
        ParseResult<string> result = ParseResult<string>.Ok("hello");
        await Assert.That(result.Value).IsEqualTo("hello");
    }

    [Test]
    public async Task Ok_TryGetValue_ReturnsTrueAndValue()
    {
        ParseResult<int> result = ParseResult<int>.Ok(7);
        bool ok = result.TryGetValue(out int value);
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(7);
    }

    [Test]
    public async Task Ok_TryGetError_ReturnsFalse()
    {
        ParseResult<int> result = ParseResult<int>.Ok(0);
        bool hasError = result.TryGetError(out ParseError _);
        await Assert.That(hasError).IsFalse();
    }

    [Test]
    public async Task Ok_ToString_ContainsValue()
    {
        ParseResult<int> result = ParseResult<int>.Ok(99);
        await Assert.That(result.ToString()).IsEqualTo("Ok(99)");
    }

    // === Failure path ===

    [Test]
    public async Task Fail_IsError_IsTrue()
    {
        ParseResult<int> result = ParseResult<int>.Fail(ParseError.InsufficientData("test"));
        await Assert.That(result.IsError).IsTrue();
    }

    [Test]
    public async Task Fail_IsSuccess_IsFalse()
    {
        ParseResult<int> result = ParseResult<int>.Fail(ParseError.InsufficientData("test"));
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Fail_Error_ReturnsStoredError()
    {
        ParseError error = ParseError.InvalidData("proto", "bad packet");
        ParseResult<int> result = ParseResult<int>.Fail(error);
        await Assert.That(result.Error.Kind).IsEqualTo(ParseErrorKind.InvalidData);
        await Assert.That(result.Error.ProtocolName).IsEqualTo("proto");
        await Assert.That(result.Error.Message).IsEqualTo("bad packet");
    }

    [Test]
    public async Task Fail_TryGetError_ReturnsTrueAndError()
    {
        ParseError error = ParseError.Custom("p", "oops");
        ParseResult<int> result = ParseResult<int>.Fail(error);
        bool hasError = result.TryGetError(out ParseError retrieved);
        await Assert.That(hasError).IsTrue();
        await Assert.That(retrieved.Message).IsEqualTo("oops");
    }

    [Test]
    public async Task Fail_TryGetValue_ReturnsFalse()
    {
        ParseResult<int> result = ParseResult<int>.Fail(ParseError.InsufficientData("p"));
        bool ok = result.TryGetValue(out int value);
        await Assert.That(ok).IsFalse();
        await Assert.That(value).IsEqualTo(default(int));
    }

    [Test]
    public async Task Fail_ToString_ContainsErrorMessage()
    {
        ParseResult<int> result = ParseResult<int>.Fail(ParseError.Custom("p", "boom"));
        await Assert.That(result.ToString()).IsEqualTo("Error(boom)");
    }

    // === Throwing accessors ===

    [Test]
    public async Task Ok_Error_Throws()
    {
        ParseResult<int> result = ParseResult<int>.Ok(1);
        await Assert.That(() => result.Error).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Fail_Value_Throws()
    {
        ParseResult<int> result = ParseResult<int>.Fail(ParseError.InsufficientData("p"));
        await Assert.That(() => result.Value).Throws<InvalidOperationException>();
    }

    // === Implicit conversions ===

    [Test]
    public async Task ImplicitFromValue_ProducesSuccessResult()
    {
        ParseResult<string> result = "implicit";
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("implicit");
    }

    [Test]
    public async Task ImplicitFromParseError_ProducesFailureResult()
    {
        ParseResult<string> result = ParseError.InsufficientData("eth");
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Error.Kind).IsEqualTo(ParseErrorKind.InsufficientData);
    }
}