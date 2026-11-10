// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The contract of <see cref="LazyString"/>: built only on first read, built once however many copies exist, one string for
/// concurrent first readers (of one location or of different copies), a copy keeps the deferred object, a faulting factory
/// caches empty, and absent versus empty.
/// </summary>
internal sealed class LazyStringSemanticsTests
{
    [Test]
    public async Task FactoryDoesNotRunUntilTheFirstRead()
    {
        int lazyCalls = 0;
        int formatCalls = 0;
        LazyString viaFunc = LazyString.Lazy(() =>
        {
            Interlocked.Increment(ref lazyCalls);
            return "func";
        });
        LazyString viaFormat = LazyString.FormatLazy(
            formatCalls,
            _ =>
            {
                Interlocked.Increment(ref formatCalls);
                return "format";
            });

        FieldValue stored = FieldValue.NewU64(1, viaFunc);
        FieldValue storedFormat = FieldValue.NewU64(2, viaFormat);
        int beforeRead = lazyCalls + formatCalls;
        string first = stored.ToString(null, CultureInfo.InvariantCulture);
        string second = storedFormat.ToString(null, CultureInfo.InvariantCulture);

        await Assert.That(beforeRead).IsEqualTo(0);
        await Assert.That(first).IsEqualTo("func");
        await Assert.That(second).IsEqualTo("format");
        await Assert.That(lazyCalls).IsEqualTo(1);
        await Assert.That(formatCalls).IsEqualTo(1);
    }

    [Test]
    public async Task FactoryRunsOnceAndEveryReadReturnsTheSameString()
    {
        int calls = 0;
        LazyString[] storage = [LazyString.Lazy(() => new string('a', 3 + Interlocked.Increment(ref calls)))];
        bool lazyBefore = storage[0].IsLazy;

        string viaAsString = storage[0].AsString;
        string viaToString = storage[0].ToString();
        string viaImplicit = storage[0];
        string viaSpan = storage[0].AsSpan.ToString();
        int length = storage[0].Length;

        await Assert.That(lazyBefore).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(ReferenceEquals(viaAsString, viaToString)).IsTrue();
        await Assert.That(ReferenceEquals(viaAsString, viaImplicit)).IsTrue();
        await Assert.That(viaSpan).IsEqualTo(viaAsString);
        await Assert.That(length).IsEqualTo(viaAsString.Length);
        await Assert.That(storage[0].IsEvaluated).IsTrue();
        await Assert.That(storage[0].IsLazy).IsFalse();
    }

    [Test]
    public async Task EightConcurrentFirstReadersSeeOneString()
    {
        int calls = 0;
        LazyString[] storage = [LazyString.Lazy(() =>
        {
            Interlocked.Increment(ref calls);
            return new string('x', 16);
        })];

        // Dedicated threads, not Task.Run: all eight must be alive at the barrier at the same time. Pool tasks that block on
        // a barrier starve a pool with fewer idle threads than readers (the pool then grows by about one thread per second),
        // which would make this test take seconds on a small machine.
        const int readerCount = 8;
        using Barrier barrier = new(readerCount);
        string[] results = new string[readerCount];
        Thread[] readers = new Thread[readerCount];
        for (int i = 0; i < readers.Length; i++)
        {
            int slot = i;
            readers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                results[slot] = storage[0].AsString;
            })
            {
                IsBackground = true,
                Name = "LazyStringReader" + slot.ToString(CultureInfo.InvariantCulture),
            };
            readers[i].Start();
        }

        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        string later = storage[0].AsString;

        await Assert.That(calls).IsGreaterThanOrEqualTo(1);
        await Assert.That(calls).IsLessThanOrEqualTo(8);
        foreach (string result in results)
        {
            await Assert.That(ReferenceEquals(result, results[0])).IsTrue();
        }

        await Assert.That(ReferenceEquals(later, results[0])).IsTrue();
    }

    [Test]
    public async Task EvaluatingACopyKeepsTheOriginalDeferredButBuildsTheStringOnlyOnce()
    {
        int calls = 0;
        LazyString[] storage = [LazyString.Lazy(() =>
        {
            Interlocked.Increment(ref calls);
            return "copy";
        })];
        LazyString copy = storage[0];

        string fromCopy = copy.AsString;
        bool originalStillLazy = storage[0].IsLazy;
        string fromOriginal = storage[0].AsString;

        await Assert.That(fromCopy).IsEqualTo("copy");
        await Assert.That(originalStillLazy).IsTrue();
        await Assert.That(ReferenceEquals(fromCopy, fromOriginal)).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task EveryCopyOfAFormatLazyTextSharesOneBuild()
    {
        int[] calls = new int[1];
        LazyString original = LazyString.FormatLazy(
            calls,
            static counter =>
            {
                Interlocked.Increment(ref counter[0]);
                return new string('f', 5);
            });
        LazyString copyA = original;
        LazyString copyB = original;

        string fromB = copyB.AsString;
        string fromA = copyA.AsString;
        string fromOriginal = original.AsString;

        await Assert.That(calls[0]).IsEqualTo(1);
        await Assert.That(ReferenceEquals(fromA, fromB)).IsTrue();
        await Assert.That(ReferenceEquals(fromA, fromOriginal)).IsTrue();
    }

    [Test]
    public async Task ConcurrentFirstReadersOfDifferentCopiesSeeOneString()
    {
        int[] calls = new int[1];
        LazyString original = LazyString.FormatLazy(
            calls,
            static counter =>
            {
                Interlocked.Increment(ref counter[0]);
                return new string('c', 16);
            });

        // Each reader owns its copy, so the only shared thing is the deferred object: the memo there must hand every thread
        // the same instance. Dedicated threads for the barrier, as in EightConcurrentFirstReadersSeeOneString.
        const int readerCount = 8;
        using Barrier barrier = new(readerCount);
        string[] results = new string[readerCount];
        Thread[] readers = new Thread[readerCount];
        for (int i = 0; i < readers.Length; i++)
        {
            int slot = i;
            LazyString copy = original;
            readers[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                results[slot] = copy.AsString;
            })
            {
                IsBackground = true,
                Name = "LazyStringCopyReader" + slot.ToString(CultureInfo.InvariantCulture),
            };
            readers[i].Start();
        }

        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        await Assert.That(calls[0]).IsGreaterThanOrEqualTo(1);
        await Assert.That(calls[0]).IsLessThanOrEqualTo(readerCount);
        foreach (string result in results)
        {
            await Assert.That(ReferenceEquals(result, results[0])).IsTrue();
        }

        await Assert.That(ReferenceEquals(original.AsString, results[0])).IsTrue();
    }

    [Test]
    public async Task ThrowingFactoryCachesEmptyAndReportsTheError()
    {
        int calls = 0;
        LazyString[] storage = [LazyString.Lazy(() =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        })];

        bool first = storage[0].TryGetString(out string firstResult, out LazyStringError error);
        bool second = storage[0].TryGetString(out string secondResult, out LazyStringError secondError);

        await Assert.That(first).IsFalse();
        await Assert.That(firstResult).IsEqualTo(string.Empty);
        await Assert.That(error.Code).IsEqualTo(LazyStringErrorCode.FactoryFaulted);
        await Assert.That(error.Message).IsEqualTo("boom");
        await Assert.That(second).IsTrue();
        await Assert.That(secondResult).IsEqualTo(string.Empty);
        await Assert.That(secondError.Code).IsEqualTo(LazyStringErrorCode.None);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task AsStringOnAFreshThrowingInstancePropagatesTheException()
    {
        LazyString lazy = LazyString.Lazy(static () => throw new InvalidOperationException("boom"));

        await Assert.That(() => lazy.AsString).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DefaultIsAbsentAndEmptyIsEmptyBothReadAsEmptyText()
    {
        LazyString absent = default;
        LazyString empty = LazyString.Empty;

        await Assert.That(absent.IsNull).IsTrue();
        await Assert.That(absent.IsEmpty).IsTrue();
        await Assert.That(absent.AsString).IsEqualTo(string.Empty);
        await Assert.That(absent.Length).IsEqualTo(0);
        await Assert.That(absent.Equals(default)).IsTrue();
        await Assert.That(absent.Equals(empty)).IsFalse();
        await Assert.That(absent.CompareTo(empty)).IsLessThan(0);
        await Assert.That(empty.IsNull).IsFalse();
        await Assert.That(empty.IsEmpty).IsTrue();
        await Assert.That(empty.AsString).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AppendOfAbsentReturnsTheOtherSideAndTwoTextsConcatenate()
    {
        LazyString left = new("ab");
        LazyString right = new("cd");

        LazyString withAbsentRight = left.Append(default);
        LazyString withAbsentLeft = default(LazyString).Append(right);
        LazyString joined = left.Append(right);

        await Assert.That(withAbsentRight.AsString).IsEqualTo("ab");
        await Assert.That(withAbsentLeft.AsString).IsEqualTo("cd");
        await Assert.That(joined.AsString).IsEqualTo("abcd");
        await Assert.That(joined.IsEvaluated).IsTrue();
    }

    [Test]
    public async Task BoxedLazyTextInFieldValuesCachesInTheSharedBox()
    {
        int calls = 0;
        FieldValue first = FieldValue.NewLazyString(LazyString.Lazy(() =>
        {
            Interlocked.Increment(ref calls);
            return "shared";
        }));
        FieldValue second = first; // a struct copy shares the one box that holds the cache

        string fromFirst = first.ToString(null, CultureInfo.InvariantCulture);
        string fromSecond = second.ToString(null, CultureInfo.InvariantCulture);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(ReferenceEquals(fromFirst, fromSecond)).IsTrue();
    }
}
