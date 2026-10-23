// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Apply and reset for named settings, and their events.</summary>
internal sealed class SettingsApplyKeysTests
{
    #region Helpers

    private static SettingsManager _Manager(out Setting a, out Setting b)
    {
        SettingsManager manager = new();
        a = Setting.I64("g.a", "A", "g", 0);
        b = Setting.I64("g.b", "B", "g", 0);
        manager.RegisterSetting(a);
        manager.RegisterSetting(b);
        a.SetPendingValue(SettingValue.I64(1));
        b.SetPendingValue(SettingValue.I64(2));
        return manager;
    }

    #endregion

    #region ApplyChanges(names)

    [Test]
    public async Task OnlyTheNamedSettingIsAppliedAndTheEventListsIt()
    {
        using SettingsManager manager = _Manager(out Setting a, out Setting b);
        List<IReadOnlyList<string>> events = [];
        manager.SettingsApplied += (_, e) => events.Add(e.ChangedSettings);

        IReadOnlyList<string> changed = manager.ApplyChanges(["g.a"]);

        await Assert.That(changed).IsEquivalentTo(["g.a"], CollectionOrdering.Matching);
        await Assert.That(a.Value).IsEqualTo(SettingValue.I64(1));
        await Assert.That(b.Value).IsEqualTo(SettingValue.I64(0));
        await Assert.That(b.PendingValue).IsEqualTo(SettingValue.I64(2));
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsEquivalentTo(["g.a"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnEmptyListChangesNothingAndRaisesNothing()
    {
        using SettingsManager manager = _Manager(out _, out Setting b);
        int raised = 0;
        manager.SettingsApplied += (_, _) => raised++;

        IReadOnlyList<string> changed = manager.ApplyChanges([]);

        await Assert.That(changed).IsEmpty();
        await Assert.That(raised).IsEqualTo(0);
        await Assert.That(b.Value).IsEqualTo(SettingValue.I64(0));
    }

    [Test]
    public async Task AnUnknownNameThrowsBeforeAnythingIsApplied()
    {
        using SettingsManager manager = _Manager(out Setting a, out _);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => manager.ApplyChanges(["g.a", "g.nope"]));

        await Assert.That(exception.Message).Contains("g.nope");
        await Assert.That(a.Value).IsEqualTo(SettingValue.I64(0));
    }

    [Test]
    public async Task ADuplicateNameCountsOnce()
    {
        using SettingsManager manager = _Manager(out _, out _);

        IReadOnlyList<string> changed = manager.ApplyChanges(["g.a", "g.a"]);

        await Assert.That(changed).IsEquivalentTo(["g.a"], CollectionOrdering.Matching);
    }

    #endregion

    #region ResetChanges(names)

    [Test]
    public async Task ResetChangesResetsTheNamedPendingValueAndRaisesPendingChanged()
    {
        using SettingsManager manager = _Manager(out Setting a, out Setting b);
        List<string> raised = [];
        manager.PendingChanged += (_, e) => raised.Add(e.SettingName);

        manager.ResetChanges(["g.b"]);

        await Assert.That(b.PendingValue).IsEqualTo(SettingValue.I64(0));
        await Assert.That(a.PendingValue).IsEqualTo(SettingValue.I64(1));
        await Assert.That(raised).IsEquivalentTo(["g.b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResettingAnUnchangedSettingRaisesNothing()
    {
        using SettingsManager manager = _Manager(out _, out Setting b);
        manager.ApplyChanges(["g.b"]);
        int raised = 0;
        manager.PendingChanged += (_, _) => raised++;

        manager.ResetChanges(["g.b"]);

        await Assert.That(raised).IsEqualTo(0);
    }

    [Test]
    public async Task AnUnknownNameInResetThrowsAndResetsNothing()
    {
        using SettingsManager manager = _Manager(out _, out Setting b);

        await Assert.That(() => manager.ResetChanges(["g.b", "g.nope"])).Throws<ArgumentException>();
        await Assert.That(b.PendingValue).IsEqualTo(SettingValue.I64(2));
    }

    #endregion

    #region Unchanged members

    [Test]
    public async Task TheParameterlessMembersStillWorkOnEverySetting()
    {
        using SettingsManager manager = _Manager(out Setting a, out Setting b);

        IReadOnlyList<string> changed = manager.ApplyChanges();
        manager.ResetChanges();

        await Assert.That(changed).IsEquivalentTo(["g.a", "g.b"], CollectionOrdering.Matching);
        await Assert.That(a.Value).IsEqualTo(SettingValue.I64(1));
        await Assert.That(b.Value).IsEqualTo(SettingValue.I64(2));
    }

    #endregion
}
