// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The non-throwing validation and pending-value members of <see cref="Setting"/>, the activation property, and the pending
/// event of the manager.
/// </summary>
internal sealed class SettingTryValidateTests
{
    #region Helpers

    // Reads the activation through the interface, the way callers that hold only IReadOnlySetting do.
    private static SettingActivation _ActivationOf<T>(T setting)
        where T : IReadOnlySetting =>
        setting.Activation;

    #endregion

    #region TryValidate and TrySetPendingValue

    [Test]
    public async Task BadValuesGiveProblemsAndLeaveThePendingValueAlone()
    {
        Setting port = Setting.I64("t.port", "Port", "t", 100, 1, 1000);
        Setting mode = Setting.Enum("t.mode", "Mode", "t", 0, [("a", 0UL), ("b", 1UL)]);
        Setting ratio = Setting.F64("t.ratio", "Ratio", "t", 1);

        await Assert.That(port.TryValidate(SettingValue.String("x"), out SettingProblem? mismatch)).IsFalse();
        await Assert.That(port.TrySetPendingValue(SettingValue.I64(0), out SettingProblem? below)).IsFalse();
        await Assert.That(port.TrySetPendingValue(SettingValue.I64(1001), out SettingProblem? above)).IsFalse();
        await Assert.That(mode.TrySetPendingValue(SettingValue.Enum("a", 7), out SettingProblem? unknown)).IsFalse();
        await Assert.That(ratio.TrySetPendingValue(SettingValue.F64(double.NaN), out SettingProblem? nan)).IsFalse();

        await Assert.That(mismatch?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(mismatch?.Message).IsEqualTo("expected I64, got String");
        await Assert.That(below?.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
        await Assert.That(above?.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(unknown?.Kind).IsEqualTo(SettingProblemKind.UnknownEnum);
        await Assert.That(nan?.Kind).IsEqualTo(SettingProblemKind.NotFinite);
        await Assert.That(port.PendingValue).IsEqualTo(SettingValue.I64(100));
        await Assert.That(mode.PendingValue).IsEqualTo(SettingValue.Enum("a", 0));
        await Assert.That(ratio.PendingValue).IsEqualTo(SettingValue.F64(1));
    }

    [Test]
    public async Task AValidValueIsStoredAndRaisesPendingChangedOnce()
    {
        using SettingsManager manager = new();
        Setting setting = Setting.I64("t.port", "Port", "t", 100, 1, 1000);
        manager.RegisterSetting(setting);
        List<string> raised = [];
        manager.PendingChanged += (_, e) => raised.Add(e.SettingName);

        bool first = setting.TrySetPendingValue(SettingValue.I64(500), out SettingProblem? firstProblem);
        bool again = setting.TrySetPendingValue(SettingValue.I64(500), out SettingProblem? againProblem);

        await Assert.That(first).IsTrue();
        await Assert.That(firstProblem).IsNull();
        await Assert.That(again).IsTrue();
        await Assert.That(againProblem).IsNull();
        await Assert.That(setting.PendingValue).IsEqualTo(SettingValue.I64(500));
        await Assert.That(raised).IsEquivalentTo(["t.port"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AHandlerThatReadsTheManagerDoesNotDeadlock()
    {
        using SettingsManager manager = new();
        Setting setting = Setting.Bool("t.flag", "Flag", "t", false);
        manager.RegisterSetting(setting);
        int seenCount = 0;
        manager.PendingChanged += (sender, _) => seenCount = ((SettingsManager)sender!).SettingCount; // sender is the manager

        Task<bool> task = Task.Run(() => setting.TrySetPendingValue(SettingValue.Bool(true), out _));

        await Assert.That(await task.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(seenCount).IsEqualTo(1);
    }

    [Test]
    public async Task TheThrowingMemberKeepsItsExceptionTypesAndUsesTheProblemMessage()
    {
        Setting port = Setting.I64("t.port", "Port", "t", 100, 1, 1000);

        TypeMismatchSettingsException mismatch = Assert.Throws<TypeMismatchSettingsException>(() => port.SetPendingValue(SettingValue.String("x")));
        ValidationSettingsException range = Assert.Throws<ValidationSettingsException>(() => port.SetPendingValue(SettingValue.I64(0)));

        await Assert.That(mismatch).IsNotNull();
        await Assert.That(range.Message).Contains("allowed 1 – 1000");
    }

    [Test]
    public async Task ResetAndResetToDefaultRaisePendingChangedForTheChangedSettingOnly()
    {
        using SettingsManager manager = new();
        Setting changed = Setting.Bool("t.changed", "Changed", "t", false);
        Setting untouched = Setting.Bool("t.untouched", "Untouched", "t", false);
        manager.RegisterSetting(changed);
        manager.RegisterSetting(untouched);
        changed.SetPendingValue(SettingValue.Bool(true));
        List<string> raised = [];
        manager.PendingChanged += (_, e) => raised.Add(e.SettingName);

        manager.ResetChanges();
        manager.ResetAllToDefaults();

        await Assert.That(raised).IsEquivalentTo(["t.changed"], CollectionOrdering.Matching);
    }

    #endregion

    #region Activation

    [Test]
    public async Task EveryFactoryDefaultsToImmediateAndKeepsAGivenActivation()
    {
        Setting[] defaults =
        [
            Setting.Bool("t.a", "A", "t", false), Setting.String("t.b", "B", "t", ""), Setting.F64("t.c", "C", "t", 0),
            Setting.U64("t.d", "D", "t", 0), Setting.I64("t.e", "E", "t", 0), Setting.Bytes("t.f", "F", "t", []),
            Setting.Enum("t.g", "G", "t", 0, [("x", 0UL)]), Setting.BoolArray("t.h", "H", "t", []),
            Setting.StringArray("t.i", "I", "t", []), Setting.F64Array("t.j", "J", "t", []),
            Setting.U64Array("t.k", "K", "t", []), Setting.I64Array("t.l", "L", "t", []),
        ];
        Setting restart = Setting.U64("t.restart", "Restart", "t", 5, activation: SettingActivation.Restart);
        Setting session = Setting.Bool("t.session", "Session", "t", true, activation: SettingActivation.SessionReset);
        using SettingsManager manager = new();
        manager.RegisterSetting(restart);
        manager.RegisterSetting(session);

        foreach (Setting setting in defaults)
        {
            await Assert.That(setting.Activation).IsEqualTo(SettingActivation.Immediate);
        }

        await Assert.That(_ActivationOf(restart)).IsEqualTo(SettingActivation.Restart);
        await Assert.That(session.AsReadOnlyView().Activation).IsEqualTo(SettingActivation.SessionReset);
        await Assert.That(manager.ReadOnly.GetSettingsInGroup("t").Single(static s => s.Name == "t.restart").Activation)
            .IsEqualTo(SettingActivation.Restart);
    }

    [Test]
    public async Task ActivationHasExactlyTheDocumentedValues()
    {
        byte[] values = [.. Enum.GetValues<SettingActivation>().Select(static value => (byte)value)];

        await Assert.That(values).IsEquivalentTo(new byte[] { 0, 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(Enum.GetNames<SettingActivation>()).IsEquivalentTo(["Immediate", "SessionReset", "Restart"], CollectionOrdering.Matching);
    }

    #endregion
}
