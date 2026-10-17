// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using DisplayTables = NetworkInspector.Protocols.Helpers.DisplayTables;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Display texts that read alike across tables: unnamed option kinds are their number once, and the LLC SAP lookup
/// uses the full byte (odd values name group addresses).
/// </summary>
internal sealed class DisplayTablesTests
{
    #region Tests

    [Test]
    public async Task GetTcpOptionDisplayText_UnknownKind_PrintsTheNumberOnce()
    {
        await Assert.That(DisplayTables.GetTcpOptionDisplayText(16)).IsEqualTo("16");
        await Assert.That(DisplayTables.GetTcpOptionDisplayText(2)).IsEqualTo("Maximum Segment Size (2)");
        await Assert.That(DisplayTables.GetIpOptionTypeDisplayText(2)).IsEqualTo("2");
    }

    [Test]
    public async Task GetLlcSapDisplayText_OddValuesNameGroupAddresses()
    {
        await Assert.That(DisplayTables.GetLlcSapDisplayText(0x02)).IsEqualTo("Individual LLC Sublayer Management (0x02)");
        await Assert.That(DisplayTables.GetLlcSapDisplayText(0x03)).IsEqualTo("Group LLC Sublayer Management (0x03)");
        await Assert.That(DisplayTables.GetLlcSapDisplayText(0xFF)).IsEqualTo("Global DSAP (0xff)");
    }

    #endregion
}
