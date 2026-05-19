// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Exporters.Json;

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// Tests for <see cref="FieldBitmask"/> — covers the normal case and the auto-grow
/// path (field ID beyond initial capacity).
/// </summary>
internal sealed class FieldBitmaskTests
{
    [Test]
    public async Task Insert_NewFieldId_ReturnsTrue()
    {
        FieldBitmask bitmask = new(64);

        bool isNew = bitmask.Insert(0);

        await Assert.That(isNew).IsTrue();
    }

    [Test]
    public async Task Insert_SameFieldId_ReturnsFalse()
    {
        FieldBitmask bitmask = new(64);
        bitmask.Insert(5);

        bool isNew = bitmask.Insert(5);

        await Assert.That(isNew).IsFalse();
    }

    [Test]
    public async Task Insert_DifferentFieldIds_AllReturnTrue()
    {
        FieldBitmask bitmask = new(64);

        bool a = bitmask.Insert(0);
        bool b = bitmask.Insert(1);
        bool c = bitmask.Insert(63);

        await Assert.That(a).IsTrue();
        await Assert.That(b).IsTrue();
        await Assert.That(c).IsTrue();
    }

    /// <summary>
    /// Auto-grow — a field ID beyond the initial capacity must trigger array
    /// growth and still return <c>true</c> (first occurrence).
    /// </summary>
    [Test]
    public async Task Insert_FieldIdBeyondInitialCapacity_Grows_AndReturnsTrue()
    {
        // Initial capacity covers only field IDs 0..63 (one 64-bit word).
        FieldBitmask bitmask = new(64);

        // Field ID 64 is the first entry in the second word — triggers growth.
        bool isNew = bitmask.Insert(64);

        await Assert.That(isNew).IsTrue();
    }

    [Test]
    public async Task Insert_FieldIdBeyondInitialCapacity_SecondTime_ReturnsFalse()
    {
        FieldBitmask bitmask = new(64);
        bitmask.Insert(200);  // forces growth

        bool isNew = bitmask.Insert(200);

        await Assert.That(isNew).IsFalse();
    }

    [Test]
    public async Task Insert_FieldIdZeroAndBeyondCapacity_IndependentBits()
    {
        // Ensure that a write beyond capacity does not corrupt the low field IDs.
        FieldBitmask bitmask = new(64);
        bitmask.Insert(0);   // set bit 0
        bitmask.Insert(127); // forces growth; sets bit 63 in word 1

        // Bit 0 must still be set
        await Assert.That(bitmask.Insert(0)).IsFalse();

        // Bit 1 must still be clear (never set)
        await Assert.That(bitmask.Insert(1)).IsTrue();

        // Bit 127 must be set
        await Assert.That(bitmask.Insert(127)).IsFalse();
    }
}
