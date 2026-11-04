// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Conversions of the reader rule of the exporter guide (§ Rebuilding the display line), written out once for the tests that execute
/// the rule. They state the rule themselves and do not call the library's own formatting, so a changed stored form or default text
/// fails a test instead of the oracle following it.
/// </summary>
/// <remarks>
/// Thread safety: stateless.
/// Sync: NetworkInspector.Exporters/EXPORTER_GUIDE.md § Rebuilding the display line (the table of default texts).
/// </remarks>
internal static class ReaderRules
{
    #region Default texts

    /// <summary>The default text of an IPv4 address from its stored integer: the four bytes, most significant first, joined with dots.</summary>
    /// <param name="address">The big-endian reading of the address bytes (<c>0xC0A80101</c> is <c>192.168.1.1</c>).</param>
    /// <returns>The dotted decimal text.</returns>
    internal static string IPv4Text(uint address) =>
        string.Join('.', [address >> 24, (address >> 16) & 0xFF, (address >> 8) & 0xFF, address & 0xFF]);

    #endregion
}
