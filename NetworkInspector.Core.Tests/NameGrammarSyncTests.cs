// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Keeps the name rules of the settings and of the protocols in step: the settings keep a copy of the grammar so that they need
/// nothing of the protocol layer, and this test sees both.
/// </summary>
/// <remarks>
/// Sync: <see cref="SettingNames"/> and <see cref="NameValidation"/> hold the same grammar; this fixed corpus must give the same verdict
/// from both.
/// </remarks>
internal sealed class NameGrammarSyncTests
{
    [Test]
    public async Task SettingNamesAndProtocolNamesAgreeOnTheCorpus()
    {
        string[] corpus =
        [
            "ip.src", "tcp.flags.syn", "_x", "a..b", "1a", "a.", ".a", "ä", string.Empty, new string('a', 255), "a b", "A.B", "a1.b2", "x-y",
        ];

        foreach (string name in corpus)
        {
            bool settings = SettingNames.IsValidName(name.AsSpan());
            bool protocols = NameValidation.IsValidName(name.AsSpan());

            await Assert.That(Invariant($"{name}:{settings}")).IsEqualTo(Invariant($"{name}:{protocols}"));
        }
    }

    [Test]
    public async Task SettingUiNamesAndProtocolUiNamesAgreeOnTheCorpus()
    {
        string[] corpus = ["Name", string.Empty, "tab\there", "Größe", "日本語", "line\nbreak", " "];

        foreach (string uiName in corpus)
        {
            bool settings = SettingNames.IsValidUiName(uiName.AsSpan());
            bool protocols = NameValidation.IsValidUiName(uiName.AsSpan());

            await Assert.That(Invariant($"{uiName}:{settings}")).IsEqualTo(Invariant($"{uiName}:{protocols}"));
        }
    }
}
