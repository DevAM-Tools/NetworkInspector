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
    internal static bool ShouldPreload(long fileSize, long? preloadBudget) =>
        preloadBudget.HasValue && fileSize <= Math.Min(preloadBudget.Value, Array.MaxLength);

    #endregion
}
