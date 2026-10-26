// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources;

/// <summary>
/// The in-memory vs. mapped/disk backend decision shared by the PCAPNG, BLF, and ASC file sources.
/// </summary>
/// <remarks>Stateless; thread-safe.</remarks>
internal static class PreloadDecision
{
    #region Public API

    /// <summary>
    /// Returns true when a file of <paramref name="fileSize"/> bytes is loaded into one array: the budget is set,
    /// the file fits the budget, and it fits a .NET array (<see cref="Array.MaxLength"/>). A larger file uses the
    /// mapped/disk backend even with <c>PreloadBudget = long.MaxValue</c> instead of failing in
    /// <see cref="File.ReadAllBytes(string)"/>.
    /// </summary>
    /// <param name="fileSize">File size in bytes.</param>
    /// <param name="preloadBudget">The <c>PreloadBudget</c> option; null means "never preload".</param>
    /// <remarks>
    /// The <see cref="Array.MaxLength"/> cap is a hard limit of the format of the in-memory path (one <c>byte[]</c> holds
    /// the file and offsets into it are <c>int</c>), not a tuning value: no budget can lift it. The budget itself is a
    /// caller-chosen memory trade-off (see the options of each source). Callers pass the size of the file they hold open.
    /// </remarks>
    internal static bool ShouldPreload(long fileSize, long? preloadBudget) =>
        preloadBudget.HasValue && fileSize <= Math.Min(preloadBudget.Value, Array.MaxLength);

    #endregion
}
