// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>Exit-point coverage for the error types and throw helpers of the settings.</summary>
internal sealed class SettingsErrorTypesTests
{
    [Test]
    public async Task InvalidUiNameSettingsExceptionForContainsTheUiName()
    {
        InvalidUiNameSettingsException ex = InvalidUiNameSettingsException.For("bad name");

        await Assert.That(ex.UiName).IsEqualTo("bad name");
        await Assert.That(ex.Message).Contains("Invalid UI name");
    }

    [Test]
    public async Task PersistenceSettingsExceptionForIoWrapsInnerException()
    {
        IOException io = new("disk full");
        PersistenceSettingsException ex = PersistenceSettingsException.ForIo(io);

        await Assert.That(ex.InnerException).IsSameReferenceAs(io);
        await Assert.That(ex.Message).Contains("I/O error");
    }

    [Test]
    public async Task PersistenceSettingsExceptionForJsonWrapsInnerException()
    {
        JsonException json = new("bad json");
        PersistenceSettingsException ex = PersistenceSettingsException.ForJson(json);

        await Assert.That(ex.InnerException).IsSameReferenceAs(json);
        await Assert.That(ex.Message).Contains("JSON error");
    }

    [Test]
    public async Task SettingsExceptionInnerConstructorIsReachable()
    {
        InvalidOperationException inner = new("root");
        SettingsException ex = new TestSettingsException("wrapped", inner);

        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
        await Assert.That(ex.Message).IsEqualTo("wrapped");
    }

    [Test]
    public async Task ThrowHelpersThrowNonFiniteF64SettingsJsonThrowsInvalidOperationException()
    {
        MethodInfo? method = typeof(SettingValueJson).GetMethod(
            "_SettingF64ToJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        SettingValue nonFinite = SettingValue.F64(double.NaN);
        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
            () => method!.Invoke(null, [nonFinite]));
        await Assert.That(ex.InnerException).IsTypeOf<InvalidOperationException>();
        await Assert.That(ex.InnerException!.Message).Contains("F64 setting value must be finite");
    }

    [Test]
    public async Task ThrowHelpersThrowNonFiniteF64UsesInvariantCulture()
    {
        MethodInfo? method = typeof(SettingValueJson).GetMethod(
            "_SettingF64ToJson",
            BindingFlags.NonPublic | BindingFlags.Static);
        await Assert.That(method).IsNotNull();

        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            SettingValue nonFinite = SettingValue.F64(double.PositiveInfinity);
            TargetInvocationException ex = Assert.Throws<TargetInvocationException>(
                () => method!.Invoke(null, [nonFinite]));

            await Assert.That(ex.InnerException!.Message).IsEqualTo(
                "F64 setting value must be finite, got Infinity.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private sealed class TestSettingsException : SettingsException
    {
        internal TestSettingsException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
