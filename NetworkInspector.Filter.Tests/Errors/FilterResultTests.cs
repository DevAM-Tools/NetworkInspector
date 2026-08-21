// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Errors;

/// <summary>Covers <see cref="FilterResult{T}"/> success/failure behaviour and equality.</summary>
internal sealed class FilterResultTests
{
    [Test]
    public async Task Ok_ExposesValue()
    {
        FilterResult<int> result = FilterResult.Ok(42);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
        await Assert.That(result.TryGetValue(out int value)).IsTrue();
        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task Fail_ExposesError()
    {
        FilterError error = FilterError.Compiler("boom");

        FilterResult<int> result = FilterResult.Fail<int>(error);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).IsSameReferenceAs(error);
        await Assert.That(result.TryGetError(out FilterError? extracted)).IsTrue();
        await Assert.That(extracted).IsSameReferenceAs(error);
    }

    [Test]
    public async Task Value_OnFailure_Throws()
    {
        FilterResult<int> result = FilterResult.Fail<int>(FilterError.Compiler("boom"));

        await Assert.That(() => result.Value).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Error_OnSuccess_Throws()
    {
        FilterResult<int> result = FilterResult.Ok(1);

        await Assert.That(() => result.Error).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TryGetValue_OnFailure_ReturnsFalse()
    {
        FilterResult<string> result = FilterResult.Fail<string>(FilterError.Compiler("boom"));

        await Assert.That(result.TryGetValue(out string? value)).IsFalse();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task TryGetError_OnSuccess_ReturnsFalse()
    {
        FilterResult<string> result = FilterResult.Ok("ok");

        await Assert.That(result.TryGetError(out FilterError? error)).IsFalse();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task ImplicitConversion_FromValue_Succeeds()
    {
        FilterResult<int> result = 7;

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(7);
    }

    [Test]
    public async Task ImplicitConversion_FromError_Fails()
    {
        FilterResult<int> result = FilterError.Compiler("boom");

        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Equality_SameValue_IsEqual()
    {
        FilterResult<int> left = FilterResult.Ok(5);
        FilterResult<int> right = FilterResult.Ok(5);

        await Assert.That(left == right).IsTrue();
        await Assert.That(left.Equals((object)right)).IsTrue();
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task Equality_DifferentOutcome_IsNotEqual()
    {
        FilterResult<int> success = FilterResult.Ok(5);
        FilterResult<int> failure = FilterResult.Fail<int>(FilterError.Compiler("boom"));

        await Assert.That(success != failure).IsTrue();
        await Assert.That(success.Equals("not a result")).IsFalse();
    }
}
