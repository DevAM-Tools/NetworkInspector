// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The live holder of a limit: start value, changes, read-only, allocation-free reads, and concurrent readers.</summary>
internal sealed class LimitTests
{
    private const long _KiB = 1L << 10;

    private const long _MiB = 1L << 20;

    private const long _GiB = 1L << 30;

    #region Start and change

    [Test]
    public async Task AFreshHolderShowsTheDefaultAsValueAndAsCeiling()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        LimitWriter limit = new(definition);

        await Assert.That(limit.Limit.Definition).IsSameReferenceAs(definition);
        await Assert.That(limit.Limit.Value).IsEqualTo(definition.DefaultValue);
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(128 * _MiB);
        await Assert.That(limit.Limit.IsOff).IsFalse();
    }

    [Test]
    [Arguments(_KiB)]
    [Arguments(_GiB)]
    [Arguments(5 * _MiB)]
    public async Task AValueInsideTheRangeAndBothEndsAreAcceptedAndShownAtOnce(long number)
    {
        LimitWriter limit = new(LimitTestDefinitions.Bytes(minimum: _KiB, maximum: _GiB));

        bool set = limit.TrySet(LimitValue.Of(number), out string? problem);

        await Assert.That(set).IsTrue();
        await Assert.That(problem).IsNull();
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(number);
        await Assert.That(limit.Limit.Value).IsEqualTo(LimitValue.Of(number));
    }

    [Test]
    public async Task OffIsAcceptedWhenAllowedAndTheCeilingIsTheLargestLong()
    {
        LimitWriter limit = new(LimitTestDefinitions.Bytes());

        bool set = limit.TrySet(LimitValue.Off, out _);

        await Assert.That(set).IsTrue();
        await Assert.That(limit.Limit.IsOff).IsTrue();
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(long.MaxValue);
        await Assert.That(limit.Limit.Value).IsEqualTo(LimitValue.Off);
    }

    [Test]
    [Arguments(_KiB - 1)]
    [Arguments(_GiB + 1)]
    public async Task AValueOutsideTheRangeIsRefusedWithTheKeyAndTheRangeAndTheValueStays(long number)
    {
        LimitWriter limit = new(LimitTestDefinitions.Bytes(minimum: _KiB, maximum: _GiB));

        bool set = limit.TrySet(LimitValue.Of(number), out string? problem);

        await Assert.That(set).IsFalse();
        await Assert.That(problem).IsEqualTo("demo.maxSize: allowed 1 KiB – 1 GiB, or off");
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task OffIsRefusedWhenTheDefinitionDoesNotAllowItAndTheValueStays()
    {
        LimitWriter limit = new(LimitTestDefinitions.Bytes(allowOff: false));

        bool set = limit.TrySet(LimitValue.Off, out string? problem);

        await Assert.That(set).IsFalse();
        await Assert.That(problem).Contains("off is not accepted");
        await Assert.That(limit.Limit.IsOff).IsFalse();
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task ANullDefinitionIsRefusedWithTheNullException()
    {
        await Assert.That(() => new LimitWriter(null!)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    [Test]
    public async Task TheWriterAlwaysHandsOutTheSameReadSide()
    {
        LimitWriter writer = new(LimitTestDefinitions.Bytes());

        await Assert.That(writer.Limit).IsSameReferenceAs(writer.Limit);
    }

    [Test]
    public async Task TheReadSideOffersNoPublicConstructorAndNoPublicWayToChangeTheValue()
    {
        System.Reflection.BindingFlags declared =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;

        string[] publicMethods = [.. typeof(Limit).GetMethods(declared).Select(static method => method.Name).Order(StringComparer.Ordinal)];
        System.Reflection.PropertyInfo[] settable =
            [.. typeof(Limit).GetProperties(declared).Where(static property => property.GetSetMethod() is not null)];

        await Assert.That(typeof(Limit).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)).IsEmpty();
        await Assert.That(publicMethods).IsEquivalentTo(["get_Ceiling", "get_Definition", "get_IsOff", "get_Value"]);
        await Assert.That(settable).IsEmpty();
    }

    [Test]
    public async Task TheWriterChangesWhatTheReadSideShows()
    {
        LimitWriter writer = new(LimitTestDefinitions.Bytes());
        Limit read = writer.Limit;

        bool set = writer.TrySet(LimitValue.Of(4 * _MiB), out _);

        await Assert.That(set).IsTrue();
        await Assert.That(read.Ceiling).IsEqualTo(4 * _MiB);
    }

    #endregion

    #region Read-only

    [Test]
    public async Task AReadOnlyHolderRefusesEveryValueButTheDefault()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(readOnly: true);
        LimitWriter limit = new(definition);

        bool other = limit.TrySet(LimitValue.Of(5 * _MiB), out string? problem);
        bool sameAsDefault = limit.TrySet(definition.DefaultValue, out string? defaultProblem);

        await Assert.That(definition.ReadOnly).IsTrue();
        await Assert.That(other).IsFalse();
        await Assert.That(problem).IsEqualTo("demo.maxSize: the limit is read-only");
        await Assert.That(sameAsDefault).IsTrue();
        await Assert.That(defaultProblem).IsNull();
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(128 * _MiB);
    }

    [Test]
    public async Task AReadOnlyHolderThatMayBeSwitchedOffStillRefusesOffBecauseOffIsAChange()
    {
        LimitWriter limit = new(LimitTestDefinitions.Bytes(allowOff: true, readOnly: true));

        bool set = limit.TrySet(LimitValue.Off, out string? problem);

        await Assert.That(set).IsFalse();
        await Assert.That(problem).Contains("read-only");
        await Assert.That(limit.Limit.IsOff).IsFalse();
    }

    #endregion

    #region Reads on the hot path

    /// <summary>Reads the ceiling a million times and returns the bytes this thread allocated while doing so.</summary>
    /// <param name="limit">The holder to read.</param>
    /// <returns>The smallest number of allocated bytes of five rounds, so that a one-time JIT or tiering allocation does not count.</returns>
    private static long _AllocatedByAMillionReads(Limit limit)
    {
        long smallest = long.MaxValue;
        long sum = 0;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int read = 0; read < 1_000_000; read++)
            {
                sum += limit.Ceiling;
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            smallest = Math.Min(smallest, after - before);
        }

        // The sum keeps the loop from being optimized away; it is never negative here.
        GC.KeepAlive(sum);
        return smallest;
    }

    [Test]
    public async Task ReadingTheCeilingAllocatesNothingForAnActiveAndForAnOffLimit()
    {
        LimitWriter active = new(LimitTestDefinitions.Bytes());
        LimitWriter off = new(LimitTestDefinitions.Bytes());
        off.TrySet(LimitValue.Off, out _);

        long activeBytes = _AllocatedByAMillionReads(active.Limit);
        long offBytes = _AllocatedByAMillionReads(off.Limit);

        await Assert.That(activeBytes).IsEqualTo(0L);
        await Assert.That(offBytes).IsEqualTo(0L);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task ReadersNeverSeeATornOrInvalidValueWhileAWriterAlternates()
    {
        const long valueA = 4 * _MiB;
        const long valueB = 64 * _MiB;
        const int writes = 100_000;
        LimitDefinition definition = LimitTestDefinitions.Bytes(minimum: _KiB, maximum: _GiB);
        LimitWriter limit = new(definition);
        using ManualResetEventSlim start = new(false);

        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < writes; index++)
            {
                limit.TrySet(LimitValue.Of(index % 2 == 0
                    ? valueA
                    : valueB), out _);
            }
        });

        Task<long>[] readers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            long invalid = 0;
            for (int index = 0; index < writes; index++)
            {
                long ceiling = limit.Limit.Ceiling;
                long value = limit.Limit.Value.Value;

                // Before the first write the holder shows the default; after it only A or B (the writer never writes anything else).
                bool ceilingOk = ceiling is valueA or valueB || ceiling == definition.Default;
                bool valueOk = value is valueA or valueB || value == definition.Default;
                if (!ceilingOk || !valueOk)
                {
                    invalid++;
                }
            }

            return invalid;
        }))];
        start.Set();
        await writer;
        long[] invalidCounts = await Task.WhenAll(readers);

        await Assert.That(invalidCounts.Sum()).IsEqualTo(0L);
        await Assert.That(limit.Limit.Ceiling).IsEqualTo(valueB);
    }

    #endregion
}
