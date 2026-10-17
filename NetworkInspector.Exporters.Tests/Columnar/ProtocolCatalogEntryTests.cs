// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Columnar;

/// <summary>Tests for <see cref="ProtocolCatalogEntry.FromStack"/>, the source of the <c>protocols</c> catalog table.</summary>
internal sealed class ProtocolCatalogEntryTests
{
    #region Tests

    /// <summary>Every registered protocol becomes one entry with its id, name and display name, in id order.</summary>
    [Test]
    public async Task FromStackListsEveryRegisteredProtocol()
    {
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);
        ReadOnlyMemory<ProtocolInfo> protocols = capture.Stack.Protocols;

        ProtocolCatalogEntry[] entries = ProtocolCatalogEntry.FromStack(capture.Stack);

        await Assert.That(entries.Length).IsEqualTo(protocols.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            ProtocolInfo info = protocols.Span[i];
            await Assert.That(entries[i]).IsEqualTo(new ProtocolCatalogEntry(info.Id.Value, info.Name, info.UiName));
        }
    }

    /// <summary>A protocol added by the caller appears with its own name and display name.</summary>
    [Test]
    public async Task FromStackIncludesCustomProtocol()
    {
        using DescribedFieldCapture capture = DescribedFieldCapture.Create(1);

        ProtocolCatalogEntry[] entries = ProtocolCatalogEntry.FromStack(capture.Stack);
        ProtocolCatalogEntry? custom = Array.Find(
            entries, static entry => string.Equals(entry.Name, DescribedFieldCapture.ProtocolName, StringComparison.Ordinal));

        await Assert.That(custom).IsNotNull();
        await Assert.That(custom!.UiName).IsEqualTo(DescribedFieldCapture.ProtocolUiName);
    }

    /// <summary>A null stack is rejected at entry.</summary>
    [Test]
    public async Task FromStackNullThrows()
    {
        await Assert.That(() => ProtocolCatalogEntry.FromStack(null!)).Throws<ArgumentNullException>();
    }

    #endregion
}
