// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits.Tests;

/// <summary>The hit sentence of a limit: the text, the trimming of the event text, and the required hint.</summary>
internal sealed class LimitMessagesTests
{
    private const long _MiB = 1L << 20;

    private const string _Hint = "Change it with app set archive.maxEntrySize <value>.";

    [Test]
    public async Task TheHitTextNamesTheValueTheKeyAndTheHintOfTheApplication()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes(key: "archive.maxEntrySize");

        string message = definition.HitMessage("Container is 300 MiB", LimitValue.Of(128 * _MiB), _Hint);

        await Assert.That(message).IsEqualTo(
            "Container is 300 MiB. Limit: 128 MiB (setting archive.maxEntrySize). Change it with app set archive.maxEntrySize <value>.");
    }

    [Test]
    [Arguments("Container is 300 MiB.")]
    [Arguments("Container is 300 MiB.  ")]
    public async Task AFinalPeriodOfTheEventTextIsNotDoubled(string eventText)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        string message = definition.HitMessage(eventText, LimitValue.Of(128 * _MiB), _Hint);

        await Assert.That(message).StartsWith("Container is 300 MiB. Limit: 128 MiB");
    }

    [Test]
    public async Task OffAsTheEffectiveValueIsFormattedAsOff()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        string message = definition.HitMessage("Entry is 300 MiB", LimitValue.Off, _Hint);

        await Assert.That(message).Contains("Limit: off (setting demo.maxSize)");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("...")]
    public async Task AnEventTextWithoutWordsIsRefused(string eventText)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        await Assert.That(() => definition.HitMessage(eventText, LimitValue.Of(1), _Hint)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("Change it with app set x")]
    [Arguments("Change it with app set x.  ")]
    public async Task AHintThatIsBlankOrDoesNotEndInAPeriodIsRefused(string hint)
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        await Assert.That(() => definition.HitMessage("Entry is 300 MiB", LimitValue.Of(1), hint)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ANullArgumentIsRefusedWithTheNullException()
    {
        LimitDefinition definition = LimitTestDefinitions.Bytes();

        // null! is the input under test in each of the three calls
        await Assert.That(() => definition.HitMessage(null!, LimitValue.Of(1), _Hint)).Throws<ArgumentNullException>();
        await Assert.That(() => definition.HitMessage("Entry is 300 MiB", LimitValue.Of(1), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => LimitMessages.HitMessage(null!, "Entry is 300 MiB", LimitValue.Of(1), _Hint)).Throws<ArgumentNullException>();
    }
}
