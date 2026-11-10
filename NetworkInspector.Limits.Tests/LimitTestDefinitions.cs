// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>Builds valid limit definitions for the limit tests; a test overrides only the argument it is about.</summary>
internal static class LimitTestDefinitions
{
    #region Texts

    /// <summary>A complete set of valid texts.</summary>
    /// <returns>Seven non-empty texts; every text except the title ends with a period.</returns>
    internal static LimitTexts Texts() => new(
        Title: "Largest demo size",
        Description: "The largest size of one demo item.",
        Reason: "A hostile input could otherwise use all memory.",
        Benefit: "A broken input is refused with a clear message.",
        SmallerValues: "Items above the value are refused: 1 MiB refuses a 2 MiB item.",
        LargerValues: "More memory may be used per item: 1 GiB lets a 900 MiB item through.",
        OffEffect: "Every item is accepted and a hostile input can use all memory.");

    #endregion

    #region Definitions

    /// <summary>A byte limit with the default 128 MiB.</summary>
    /// <param name="key">The key.</param>
    /// <param name="minimum">The smallest value in bytes.</param>
    /// <param name="maximum">The largest value in bytes.</param>
    /// <param name="defaultValue">The default in bytes; 128 MiB when <see langword="null"/>.</param>
    /// <param name="texts">The texts; <see cref="Texts"/> when <see langword="null"/>.</param>
    /// <param name="allowOff">Whether the limit may be switched off.</param>
    /// <param name="readOnly">Whether the limit is fixed after creation.</param>
    /// <returns>The definition.</returns>
    internal static LimitDefinition Bytes(string key = "demo.maxSize", long minimum = 1024, long maximum = 1L << 40,
        long? defaultValue = null, LimitTexts? texts = null, bool allowOff = true, bool readOnly = false) =>
        new(key, LimitUnits.Bytes, defaultValue ?? (128L << 20), minimum, maximum, texts ?? Texts(), allowOff, readOnly);

    /// <summary>A duration limit from 1 ms to <paramref name="maximum"/> with the default 30 s.</summary>
    /// <param name="key">The key.</param>
    /// <param name="minimum">The smallest value in milliseconds.</param>
    /// <param name="maximum">The largest value in milliseconds; 24 hours by default.</param>
    /// <returns>The definition.</returns>
    internal static LimitDefinition Duration(string key = "demo.waitTime", long minimum = 1, long maximum = 24L * 3_600_000) =>
        new(key, LimitUnits.Duration, 30_000, minimum, maximum, Texts());

    /// <summary>A count limit from 1 to 1,000,000 with the default 10,000.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The definition.</returns>
    internal static LimitDefinition Count(string key = "demo.maxItems") =>
        new(key, LimitUnits.Count, 10_000, 1, 1_000_000, Texts());

    #endregion
}
