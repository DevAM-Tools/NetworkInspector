// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>Unit tests for <see cref="SignalEnumTableBuilder"/> classification.</summary>
internal sealed class SignalEnumTableBuilderTests
{
    [Test]
    public async Task TryBuild_Empty_ReturnsNone()
    {
        bool ok = SignalEnumTableBuilder.TryBuild(null, bitLength: 8, maxEnumValues: 4096, out SignalEnumTable table, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(table.Kind).IsEqualTo(SignalEnumKind.None);
    }

    [Test]
    public async Task TryBuild_DenseLow_UsesArrayIndexEqualsRaw()
    {
        Dictionary<ulong, string> map = new()
        {
            [0] = "Off",
            [1] = "On",
            [2] = "Error",
        };

        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 8, maxEnumValues: 4096, out SignalEnumTable table, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(table.Kind).IsEqualTo(SignalEnumKind.DenseLow);
        await Assert.That(table.TryGetName(1, out string? name)).IsTrue();
        await Assert.That(name).IsEqualTo("On");
        await Assert.That(table.TryGetName(9, out _)).IsFalse();
    }

    [Test]
    public async Task TryBuild_DenseHigh_FourBitTopTwoValues()
    {
        // 4-bit maxRaw=15; top two values 14,15
        Dictionary<ulong, string> map = new()
        {
            [14] = "Snac",
            [15] = "Error",
        };

        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 4, maxEnumValues: 4096, out SignalEnumTable table, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(table.Kind).IsEqualTo(SignalEnumKind.DenseHigh);
        await Assert.That(table.TryGetName(15, out string? name)).IsTrue();
        await Assert.That(name).IsEqualTo("Error");
        await Assert.That(table.TryGetName(14, out name)).IsTrue();
        await Assert.That(name).IsEqualTo("Snac");
        await Assert.That(table.TryGetName(13, out _)).IsFalse();
    }

    [Test]
    public async Task TryBuild_Sparse_UsesFrozenDictionary()
    {
        Dictionary<ulong, string> map = new()
        {
            [0] = "A",
            [5] = "B",
        };

        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 8, maxEnumValues: 4096, out SignalEnumTable table, out string? error);
        await Assert.That(ok).IsTrue();
        await Assert.That(table.Kind).IsEqualTo(SignalEnumKind.Sparse);
        await Assert.That(table.TryGetName(5, out string? name)).IsTrue();
        await Assert.That(name).IsEqualTo("B");
    }

    [Test]
    public async Task TryBuild_MaxEnumValuesNotPositive_Fails()
    {
        Dictionary<ulong, string> map = new() { [0] = "A" };
        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 8, maxEnumValues: 0, out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("greater than zero");
    }

    [Test]
    public async Task TryBuild_BitLengthOutOfRange_Fails()
    {
        Dictionary<ulong, string> map = new() { [0] = "A" };
        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 0, maxEnumValues: 8, out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("bit_length");
    }

    [Test]
    public async Task TryGetName_None_ReturnsFalse()
    {
        await Assert.That(SignalEnumTable.None.TryGetName(0, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_DenseLow_NullSlot_ReturnsFalse()
    {
        SignalEnumTable table = SignalEnumTable.CreateDenseLow([null!]);
        await Assert.That(table.TryGetName(0, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_DenseHigh_RawAboveMax_ReturnsFalse()
    {
        SignalEnumTable table = SignalEnumTable.CreateDenseHigh(["Top"], 15);
        await Assert.That(table.TryGetName(16, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_DenseHigh_IndexPastArray_ReturnsFalse()
    {
        SignalEnumTable table = SignalEnumTable.CreateDenseHigh(["Top"], 15);
        await Assert.That(table.TryGetName(0, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_DenseHigh_NullSlot_ReturnsFalse()
    {
        SignalEnumTable table = SignalEnumTable.CreateDenseHigh([null!], 15);
        await Assert.That(table.TryGetName(15, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_DenseHigh_NullArray_ReturnsFalse()
    {
        SignalEnumTable table = new(SignalEnumKind.DenseHigh, null, null, 15);
        await Assert.That(table.TryGetName(15, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_Sparse_MissingKey_ReturnsFalse()
    {
        Dictionary<ulong, string> map = new() { [1] = "A" };
        bool ok = SignalEnumTableBuilder.TryBuild(map, 8, 8, out SignalEnumTable table, out _);
        await Assert.That(ok).IsTrue();
        await Assert.That(table.TryGetName(2, out _)).IsFalse();
    }

    [Test]
    public async Task TryGetName_Sparse_NullMap_ReturnsFalse()
    {
        SignalEnumTable table = new(SignalEnumKind.Sparse, null, null, 0);
        await Assert.That(table.TryGetName(1, out _)).IsFalse();
    }

    [Test]
    public async Task IsDenseHigh_EmptyKeys_ReturnsFalse()
    {
        await Assert.That(SignalEnumTableBuilder.IsDenseHigh([], 15)).IsFalse();
    }

    [Test]
    public async Task IsDenseHigh_RangeDoesNotFit_ReturnsFalse()
    {
        await Assert.That(SignalEnumTableBuilder.IsDenseHigh([0, 1, 2], 1)).IsFalse();
    }

    [Test]
    public async Task IsDenseHigh_GapInSequence_ReturnsFalse()
    {
        await Assert.That(SignalEnumTableBuilder.IsDenseHigh([13, 15], 15)).IsFalse();
    }

    [Test]
    public async Task IsDenseHigh_MiddleGap_ReturnsFalse()
    {
        // First key matches expectedFirst (13 for n=3, maxRaw=15) but the sequence has a hole.
        await Assert.That(SignalEnumTableBuilder.IsDenseHigh([13, 15, 16], 15)).IsFalse();
    }

    [Test]
    public async Task TryBuild_DuplicateKeysInEnumeration_Fails()
    {
        bool ok = SignalEnumTableBuilder.TryBuild(
            new DuplicateKeyMap(),
            bitLength: 8,
            maxEnumValues: 8,
            out _,
            out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Duplicate enum key");
    }

    [Test]
    public async Task TryBuild_ExceedsCap_Fails()
    {
        Dictionary<ulong, string> map = new();
        for (ulong i = 0; i < 5; i++)
        {
            map[i] = "V" + i.ToString(CultureInfo.InvariantCulture);
        }

        bool ok = SignalEnumTableBuilder.TryBuild(map, bitLength: 8, maxEnumValues: 4, out _, out string? error);
        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    /// <summary>
    /// <see cref="Dictionary{TKey, TValue}"/> cannot store duplicate keys; this map yields the same
    /// key twice so the builder's sorted-key uniqueness check is reachable.
    /// </summary>
    private sealed class DuplicateKeyMap : IReadOnlyDictionary<ulong, string>
    {
        public int Count => 2;

        public string this[ulong key] => "A";

        public IEnumerable<ulong> Keys
        {
            get
            {
                yield return 1;
                yield return 1;
            }
        }

        public IEnumerable<string> Values
        {
            get
            {
                yield return "A";
                yield return "A";
            }
        }

        public bool ContainsKey(ulong key) => key == 1UL;

        public bool TryGetValue(ulong key, out string value)
        {
            value = "A";
            return key == 1UL;
        }

        public IEnumerator<KeyValuePair<ulong, string>> GetEnumerator()
        {
            yield return new(1UL, "A");
            yield return new(1UL, "B");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
