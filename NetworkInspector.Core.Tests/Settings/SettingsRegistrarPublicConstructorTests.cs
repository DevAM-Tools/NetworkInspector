// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// The public constructor of <see cref="SettingsRegistrar"/> and the <see cref="SettingScope"/> enumeration. Plugins create a
/// registrar over a manager they own, so the constructor must validate and a <c>default</c> registrar must fail clearly.
/// </summary>
internal sealed class SettingsRegistrarPublicConstructorTests
{
    #region Constants

    private static readonly byte[] _ScopeValues = [0, 1, 2];

    private static readonly string[] _ScopeNames = ["Application", "Profile", "Session"];

    #endregion

    #region Registration into the given manager

    [Test]
    public async Task PublicConstructorRegistersGroupsAndSettingsIntoTheManager()
    {
        using SettingsManager manager = new();
        SettingsRegistrar registrar = new(manager);

        registrar.RegisterGroup("sample", "Sample");
        SettingRegistrationResult result = registrar.RegisterI64Setting("count", "Frames", "", 100, min: 0);
        SettingGroup group = manager.GetGroup("sample") ?? throw new InvalidOperationException("The group was not registered.");
        await Assert.That(group.UiName).IsEqualTo("Sample");
        await Assert.That(result.IsDefault).IsTrue();
        await Assert.That(manager.GetI64Setting("count")).IsEqualTo(100L);
    }

    [Test]
    public async Task RegisteringTheSameNameTwiceThrowsDuplicateName()
    {
        using SettingsManager manager = new();
        new SettingsRegistrar(manager).RegisterI64Setting("count", "Frames", "", 100);

        await Assert.That(() => new SettingsRegistrar(manager).RegisterI64Setting("count", "Frames", "", 100))
            .Throws<DuplicateNameSettingsException>();
    }

    [Test]
    public async Task StackBuilderRegistrarStillRegistersIntoTheStacksManager()
    {
        using SettingsManager manager = new();
        StackBuilder builder = new(manager, new FrameInterfaceRegistry());

        builder.SettingsRegistrar.RegisterBoolSetting("test.flag", "Flag", "test", true);

        await Assert.That(manager.GetBoolSetting("test.flag")).IsTrue();
    }

    #endregion

    #region Misuse

    [Test]
    public async Task NullManagerThrowsArgumentNull()
    {
        await Assert.That(() =>
        {
            SettingsRegistrar registrar = new(null!); // deliberate null: proves the argument guard
            registrar.RegisterGroup("a", "A");
        }).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task DefaultRegistrarFailsWithAClearMessageInsteadOfANullReference()
    {
        // A ref struct cannot forbid default(T); every member must report the missing manager the same way.
        await Assert.That(() => default(SettingsRegistrar).RegisterGroup("a", "A")).Throws<InvalidOperationException>()
            .WithMessage("The registrar was not created with a settings manager.");
        await Assert.That(() => default(SettingsRegistrar).RegisterSetting(Setting.Bool("a.b", "B", "a", true)))
            .Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterBoolSetting("a.c", "C", "a", true)).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterStringSetting("a.d", "D", "a", "x")).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterF64Setting("a.e", "E", "a", 1.5)).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterU64Setting("a.f", "F", "a", 1)).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterI64Setting("a.g", "G", "a", 1)).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterBytesSetting("a.h", "H", "a", [1])).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterEnumSetting("a.i", "I", "a", 0, EnumSettingMetadata.FromPairs([("x", 0)])))
            .Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterBoolArraySetting("a.j", "J", "a", [])).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterStringArraySetting("a.k", "K", "a", [])).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterF64ArraySetting("a.l", "L", "a", [])).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterU64ArraySetting("a.m", "M", "a", [])).Throws<InvalidOperationException>();
        await Assert.That(() => default(SettingsRegistrar).RegisterI64ArraySetting("a.n", "N", "a", [])).Throws<InvalidOperationException>();
    }

    #endregion

    #region Scope

    [Test]
    public async Task SettingScopeHasExactlyApplicationProfileAndSession()
    {
        byte[] values = [.. Enum.GetValues<SettingScope>().Select(static scope => (byte)scope)];

        await Assert.That(values).IsEquivalentTo(_ScopeValues);
        await Assert.That(Enum.GetNames<SettingScope>()).IsEquivalentTo(_ScopeNames);
        await Assert.That(Enum.GetUnderlyingType(typeof(SettingScope))).IsEqualTo(typeof(byte));
    }

    #endregion
}
