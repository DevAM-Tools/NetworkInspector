// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>Lookup by exact name and the "did you mean" suggestions of <see cref="SettingsManager"/>.</summary>
internal sealed class SettingsLookupTests
{
    #region Helpers

    private static SettingsManager _Manager()
    {
        SettingsManager manager = new();
        manager.RegisterSetting(Setting.Bool("tcp.relative_sequence_numbers", "Relative", "tcp", true));
        manager.RegisterSetting(Setting.U64("tcp.max_tracked_streams", "Streams", "tcp", 100000));
        manager.RegisterSetting(Setting.Bool("tcp.verify_checksum", "TCP checksum", "tcp", false));
        manager.RegisterSetting(Setting.Bool("udp.verify_checksum", "UDP checksum", "udp", false));
        manager.RegisterSetting(Setting.String("sample.greeting", "Greeting", "sample", "Hello"));
        return manager;
    }

    #endregion

    #region TryGetSetting

    [Test]
    public async Task ExactNamesAreFoundAndNamesAreCaseSensitive()
    {
        using SettingsManager manager = _Manager();

        bool found = manager.TryGetSetting("tcp.max_tracked_streams", out Setting? setting);
        bool otherCase = manager.TryGetSetting("TCP.max_tracked_streams", out Setting? none);

        await Assert.That(found).IsTrue();
        await Assert.That(setting?.Name).IsEqualTo("tcp.max_tracked_streams");
        await Assert.That(otherCase).IsFalse();
        await Assert.That(none).IsNull();
        await Assert.That(() => manager.TryGetSetting(null!, out _)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region FindSimilar

    [Test]
    public async Task APrefixMatchesFirst()
    {
        using SettingsManager manager = _Manager();

        IReadOnlyList<string> similar = manager.FindSimilar("tcp.relativ", 3);

        await Assert.That(similar[0]).IsEqualTo("tcp.relative_sequence_numbers");
    }

    [Test]
    public async Task ASmallEditDistanceMatchesLikeAPrefix()
    {
        using SettingsManager manager = _Manager();

        IReadOnlyList<string> similar = manager.FindSimilar("tcp.max_tracked_stream", 3);
        IReadOnlyList<string> typo = manager.FindSimilar("tcp.max_trackd_streams", 3);

        await Assert.That(similar[0]).IsEqualTo("tcp.max_tracked_streams");
        await Assert.That(typo[0]).IsEqualTo("tcp.max_tracked_streams");
    }

    [Test]
    public async Task TheLastSegmentFindsSettingsOfOtherGroupsInOrdinalOrder()
    {
        using SettingsManager manager = _Manager();

        IReadOnlyList<string> greeting = manager.FindSimilar("greeting", 3);
        IReadOnlyList<string> verify = manager.FindSimilar("verify_checksum", 3);

        await Assert.That(greeting[0]).IsEqualTo("sample.greeting");
        await Assert.That(verify).IsEquivalentTo(["tcp.verify_checksum", "udp.verify_checksum"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task NothingSimilarGivesAnEmptyListAndTheListNeverExceedsTheMaximum()
    {
        using SettingsManager manager = _Manager();

        IReadOnlyList<string> none = manager.FindSimilar("zzz", 3);
        IReadOnlyList<string> capped = manager.FindSimilar("tcp.", 2);

        await Assert.That(none).IsEmpty();
        await Assert.That(capped.Count).IsEqualTo(2);
    }

    [Test]
    public async Task APrefixIgnoresCase()
    {
        using SettingsManager manager = _Manager();

        IReadOnlyList<string> similar = manager.FindSimilar("TCP.VERIFY", 3);

        await Assert.That(similar[0]).IsEqualTo("tcp.verify_checksum");
    }

    [Test]
    public async Task AMaximumBelowOneThrows()
    {
        using SettingsManager manager = _Manager();

        await Assert.That(() => manager.FindSimilar("x", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => manager.FindSimilar(null!, 1)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion
}
