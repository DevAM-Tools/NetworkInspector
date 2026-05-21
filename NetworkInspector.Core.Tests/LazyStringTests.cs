// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for LazyString: direct mode, lazy evaluation, concatenation, implicit conversion.
/// </summary>
internal sealed class LazyStringTests
{
    [Test]
    public async Task LazyString_DirectString()
    {
        LazyString s = new("hello");
        await Assert.That(s.AsString).IsEqualTo("hello");
        await Assert.That(s.IsEvaluated).IsTrue();
    }

    [Test]
    public async Task LazyString_Empty()
    {
        LazyString s = LazyString.Empty;
        await Assert.That(s.AsString).IsEqualTo(string.Empty);
        await Assert.That(s.IsEmpty).IsTrue();
        await Assert.That(s.Length).IsEqualTo(0);
    }

    [Test]
    public async Task LazyString_LazyEvaluation()
    {
        int callCount = 0;
        LazyString s = LazyString.Lazy(() =>
        {
            callCount++;
            return "lazy result";
        });

        // Not yet evaluated
        await Assert.That(s.IsLazy).IsTrue();
        await Assert.That(callCount).IsEqualTo(0);

        // First evaluation
        string result = s.AsString;
        await Assert.That(result).IsEqualTo("lazy result");
        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task LazyString_LazyEvaluation_CachedOnMultipleAccesses()
    {
        int callCount = 0;
        LazyString s = LazyString.Lazy(() =>
        {
            callCount++;
            return "cached";
        });

        // Access multiple times
        _ = s.AsString;
        _ = s.AsString;
        _ = s.AsString;

        // Factory should only be called once
        await Assert.That(callCount).IsEqualTo(1);
        await Assert.That(s.IsEvaluated).IsTrue();
    }

    [Test]
    public async Task LazyString_FormatLazy()
    {
        int x = 42;
        LazyString s = LazyString.FormatLazy(x, static val => $"Value: {val}");
        await Assert.That(s.AsString).IsEqualTo("Value: 42");
    }

    [Test]
    public async Task LazyString_Concat()
    {
        LazyString a = new("hello");
        LazyString b = new(" world");
        LazyString combined = a.Append(b);

        // Append is now eager concatenation (struct-based, no deferred concat)
        await Assert.That(combined.AsString).IsEqualTo("hello world");
    }

    [Test]
    public async Task LazyString_Concat_EmptyOptimization()
    {
        LazyString a = new("hello");
        LazyString empty = LazyString.Empty;

        // Appending empty should return original value
        LazyString result = a.Append(empty);
        await Assert.That(result.AsString).IsEqualTo("hello");

        // Prepending to empty should return the other value
        LazyString result2 = empty.Append(a);
        await Assert.That(result2.AsString).IsEqualTo("hello");
    }

    [Test]
    public async Task LazyString_Prepend()
    {
        LazyString a = new("world");
        LazyString b = new("hello ");
        LazyString combined = a.Prepend(b);
        await Assert.That(combined.AsString).IsEqualTo("hello world");
    }

    [Test]
    public async Task LazyString_ImplicitFromString()
    {
        LazyString s = "implicit";
        await Assert.That(s.AsString).IsEqualTo("implicit");
    }

    [Test]
    public async Task LazyString_Equality()
    {
        LazyString a = new("test");
        LazyString b = new("test");
        LazyString c = new("other");

        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a == b).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task LazyString_Equality_WithNull()
    {
        LazyString a = new("test");
        // Intentionally testing Equals behavior when passed null
#pragma warning disable CA1508 // Avoid dead conditional code - testing null equality behavior
        await Assert.That(a.Equals((object?)null)).IsFalse();
#pragma warning restore CA1508
    }

    [Test]
    public async Task LazyString_Equality_ReferenceEquals()
    {
        LazyString a = new("test");
        await Assert.That(a.Equals(a)).IsTrue();
    }

    [Test]
    public async Task LazyString_CompareTo()
    {
        LazyString a = new("alpha");
        LazyString b = new("beta");
        await Assert.That(a.CompareTo(b)).IsLessThan(0);
        await Assert.That(b.CompareTo(a)).IsGreaterThan(0);
        await Assert.That(a.CompareTo(a)).IsEqualTo(0);
    }

    [Test]
    public async Task LazyString_CompareTo_Default()
    {
        LazyString a = new("test");
        LazyString absent = default;
        await Assert.That(a.CompareTo(absent)).IsGreaterThan(0);
    }

    [Test]
    public async Task LazyString_ToString()
    {
        LazyString s = new("test string");
        await Assert.That(s.ToString()).IsEqualTo("test string");
    }

    [Test]
    public async Task LazyString_Length()
    {
        LazyString s = new("12345");
        await Assert.That(s.Length).IsEqualTo(5);
    }

    [Test]
    public async Task LazyString_Concat_MultipleParts()
    {
        LazyString a = new("a");
        LazyString b = new("b");
        LazyString c = new("c");
        LazyString combined = a.Append(b).Append(c);
        await Assert.That(combined.AsString).IsEqualTo("abc");
    }

    [Test]
    public async Task LazyString_DefaultIsNull()
    {
        LazyString a = default;
        LazyString b = default;
        await Assert.That(a.IsNull).IsTrue();
        await Assert.That(a == b).IsTrue();
    }

    [Test]
    public async Task LazyString_DefaultNotEqualToValue()
    {
        LazyString a = new("test");
        LazyString b = default;
        await Assert.That(a != b).IsTrue();
        await Assert.That(a.IsNull).IsFalse();
        await Assert.That(b.IsNull).IsTrue();
    }

    // === TryGetString tests ===

    [Test]
    public async Task TryGetString_DirectString_ReturnsTrue()
    {
        LazyString s = new("hello");
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("hello");
    }

    [Test]
    public async Task TryGetString_Default_ReturnsTrueWithEmpty()
    {
        LazyString s = default;
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryGetString_LazySuccess_ReturnsTrueWithValue()
    {
        LazyString s = LazyString.Lazy(() => "lazy result");
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo("lazy result");
    }

    [Test]
    public async Task TryGetString_LazyThrows_ReturnsFalseWithEmpty()
    {
        // Factory that throws — TryGetString should catch it and return false
        LazyString s = LazyString.Lazy(() => throw new InvalidOperationException("boom"));
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsFalse();
        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TryGetString_LazyThrows_CachesEmptyToPreventRetryForever()
    {
        int callCount = 0;
        LazyString s = LazyString.Lazy(() =>
        {
            callCount++;
            throw new InvalidOperationException("boom");
        });

        // First call — should catch exception, cache empty, return false
        _ = s.TryGetString(out _);
        await Assert.That(callCount).IsEqualTo(1);

        // Second call — should see cached empty string, NOT re-invoke factory
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(string.Empty);
        await Assert.That(callCount).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetString_Empty_ReturnsTrueWithEmpty()
    {
        LazyString s = LazyString.Empty;
        bool success = s.TryGetString(out string result);
        await Assert.That(success).IsTrue();
        await Assert.That(result).IsEqualTo(string.Empty);
    }
}
