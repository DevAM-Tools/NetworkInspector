// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Shared;

/// <summary>
/// Tests for <see cref="PreloadDecision"/>, the in-memory vs. mapped backend choice of the PCAPNG, BLF, and ASC file sources.
/// </summary>
internal sealed class PreloadDecisionTests
{
    [Test]
    public async Task NoBudgetNeverPreloads()
    {
        await Assert.That(PreloadDecision.ShouldPreload(0, null)).IsFalse();
        await Assert.That(PreloadDecision.ShouldPreload(1, null)).IsFalse();
    }

    [Test]
    [Arguments(0L, 0L, true)]
    [Arguments(1L, 0L, false)]
    [Arguments(100L, 100L, true)]
    [Arguments(101L, 100L, false)]
    public async Task FileUpToTheBudgetPreloads(long fileSize, long budget, bool expected)
    {
        await Assert.That(PreloadDecision.ShouldPreload(fileSize, budget)).IsEqualTo(expected);
    }

    [Test]
    public async Task FileAboveArrayMaxLengthIsNeverPreloaded()
    {
        // An unlimited budget must not send a file into File.ReadAllBytes that no array can hold.
        await Assert.That(PreloadDecision.ShouldPreload(Array.MaxLength, long.MaxValue)).IsTrue();
        await Assert.That(PreloadDecision.ShouldPreload((long)Array.MaxLength + 1, long.MaxValue)).IsFalse();
        await Assert.That(PreloadDecision.ShouldPreload(long.MaxValue, long.MaxValue)).IsFalse();
    }
}
