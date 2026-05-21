// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="PatternResyncHeuristic"/> covering constructor validation,
/// defensive copy semantics, and basic resync behavior (regression for MEDIUM-6).
/// </summary>
internal sealed class PatternResyncHeuristicTests
{
    // === Constructor validation (regression for MEDIUM-6) ===

    [Test]
    public async Task Constructor_NullPattern_ThrowsArgumentNullException()
    {
        // Regression for MEDIUM-6: primary constructor accepted null without validation.
        // The fixed explicit constructor must reject null immediately.
        ArgumentNullException? ex = null;
        try
        {
            PatternResyncHeuristic _ = new(null!);
        }
        catch (ArgumentNullException e)
        {
            ex = e;
        }
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("pattern");
    }

    [Test]
    public async Task Constructor_EmptyPattern_ThrowsArgumentException()
    {
        // Regression for MEDIUM-6: empty pattern is meaningless for resync and must be rejected.
        ArgumentException? ex = null;
        try
        {
            PatternResyncHeuristic _ = new([]);
        }
        catch (ArgumentException e)
        {
            ex = e;
        }
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("pattern");
    }

    // === Defensive copy semantics (regression for MEDIUM-6) ===

    [Test]
    public async Task Constructor_MutatingCallerArray_DoesNotAffectResync()
    {
        // Regression for MEDIUM-6: the primary constructor stored the array by reference,
        // so a caller that mutated the array after construction could change the search pattern.
        // The fixed constructor performs a defensive copy; mutation must have no effect.
        byte[] pattern = [0xDE, 0xAD, 0xBE, 0xEF];
        PatternResyncHeuristic h = new(pattern);

        // The pattern appears at index 1 (skipping position 0 per resync contract):
        // data = [0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00]
        //                ↑ offset 1 (idx = 0 relative to data[1..])
        byte[] data = [0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x00];
        ResyncResult before = h.Resync(data);
        await Assert.That(before.IsSuccess).IsTrue();

        // Mutate caller's array — must not affect the heuristic.
        pattern[0] = 0xFF;
        pattern[1] = 0xFF;

        ResyncResult after = h.Resync(data);
        await Assert.That(after.IsSuccess).IsTrue();
        await Assert.That(after.SkipBytes).IsEqualTo(before.SkipBytes);
    }

    // === Basic resync behavior ===

    [Test]
    public async Task Resync_PatternNotPresent_ReturnsFailure()
    {
        PatternResyncHeuristic h = new([0xAA, 0xBB]);
        ResyncResult result = h.Resync([0x01, 0x02, 0x03]);
        await Assert.That(result.IsSuccess).IsFalse();
    }

    [Test]
    public async Task Resync_PatternPresentAfterFirstByte_ReturnsCorrectSkip()
    {
        // Pattern starts at byte offset 2; Resync skips byte 0 and searches from byte 1,
        // so the match is at index 1 within data[1..], meaning SkipBytes = 1 + 1 = 2.
        PatternResyncHeuristic h = new([0xAA, 0xBB]);
        byte[] data = [0x00, 0x00, 0xAA, 0xBB, 0x00];
        ResyncResult result = h.Resync(data);
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.SkipBytes).IsEqualTo(2);
    }

    [Test]
    public async Task Resync_DataTooShort_ReturnsFailure()
    {
        PatternResyncHeuristic h = new([0xAA]);
        // data.Length <= 1 → Failure per contract.
        ResyncResult result = h.Resync([0xAA]);
        await Assert.That(result.IsSuccess).IsFalse();
    }
}
