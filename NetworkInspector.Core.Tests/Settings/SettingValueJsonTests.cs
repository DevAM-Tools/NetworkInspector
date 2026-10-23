// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>The JSON form of setting values: write then read per type, wrong shapes, bounds, and the persistence form.</summary>
internal sealed class SettingValueJsonTests
{
    #region Helpers

    private static (Setting Setting, SettingValue Value)[] _Samples() =>
    [
        (Setting.Bool("t.bool", "Bool", "t", false), SettingValue.Bool(true)),
        (Setting.String("t.string", "String", "t", ""), SettingValue.String("text \"quoted\"")),
        (Setting.F64("t.f64", "F64", "t", 0), SettingValue.F64(0.1 + 0.2)),
        (Setting.U64("t.u64", "U64", "t", 0), SettingValue.U64(ulong.MaxValue)),
        (Setting.I64("t.i64", "I64", "t", 0), SettingValue.I64(long.MinValue)),
        (Setting.Bytes("t.bytes", "Bytes", "t", []), SettingValue.Bytes([1, 2, 255])),
        (Setting.Enum("t.enum", "Enum", "t", 0, [("low", 0UL), ("high", 1UL)]), SettingValue.Enum("high", 1)),
        (Setting.BoolArray("t.bools", "Bools", "t", []), SettingValue.BoolArray([true, false])),
        (Setting.StringArray("t.strings", "Strings", "t", []), SettingValue.StringArray(["a", "b"])),
        (Setting.F64Array("t.f64s", "F64s", "t", []), SettingValue.F64Array([1.5, -2])),
        (Setting.U64Array("t.u64s", "U64s", "t", []), SettingValue.U64Array([0, ulong.MaxValue])),
        (Setting.I64Array("t.i64s", "I64s", "t", []), SettingValue.I64Array([-1, long.MaxValue])),
    ];

    private static string _Write(Setting setting, SettingValue value)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            SettingValueJson.Write(writer, setting, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool _TryRead(Setting setting, string json, out SettingValue value, out SettingProblem? problem)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return SettingValueJson.TryRead(setting, document.RootElement, out value, out problem);
    }

    #endregion

    #region Round trip

    [Test]
    public async Task EveryTypeRoundTripsThroughWriteAndRead()
    {
        foreach ((Setting setting, SettingValue value) in _Samples())
        {
            string json = _Write(setting, value);
            bool read = _TryRead(setting, json, out SettingValue back, out SettingProblem? problem);

            await Assert.That($"{setting.Name}:{read}:{problem}").IsEqualTo($"{setting.Name}:True:");
            await Assert.That(back).IsEqualTo(value);
        }
    }

    [Test]
    public async Task TheWrittenFormIsThePersistenceForm()
    {
        (Setting bytes, SettingValue bytesValue) = _Samples()[5];
        (Setting enumSetting, SettingValue enumValue) = _Samples()[6];
        (Setting maximum, SettingValue maximumValue) = _Samples()[3];

        await Assert.That(_Write(bytes, bytesValue)).IsEqualTo("\"AQL/\"");
        await Assert.That(_Write(enumSetting, enumValue)).IsEqualTo("{\"name\":\"high\",\"value\":1}");
        await Assert.That(_Write(maximum, maximumValue)).IsEqualTo("18446744073709551615");
    }

    [Test]
    public async Task AnEnumerationAlsoReadsAsABareNameOrNumber()
    {
        Setting setting = Setting.Enum("t.enum", "Enum", "t", 0, [("low", 0UL), ("high", 1UL)]);

        await Assert.That(_TryRead(setting, "\"HIGH\"", out SettingValue byName, out _)).IsTrue();
        await Assert.That(_TryRead(setting, "1", out SettingValue byNumber, out _)).IsTrue();
        await Assert.That(_TryRead(setting, "\"mid\"", out _, out SettingProblem? unknown)).IsFalse();

        await Assert.That(byName).IsEqualTo(SettingValue.Enum("high", 1));
        await Assert.That(byNumber).IsEqualTo(SettingValue.Enum("high", 1));
        await Assert.That(unknown?.Kind).IsEqualTo(SettingProblemKind.UnknownEnum);
    }

    #endregion

    #region Errors

    [Test]
    public async Task WrongShapesGiveWrongType()
    {
        Setting number = Setting.I64("t.i64", "I64", "t", 0);
        Setting numbers = Setting.I64Array("t.i64s", "I64s", "t", []);

        await Assert.That(_TryRead(number, "\"x\"", out _, out SettingProblem? text)).IsFalse();
        await Assert.That(_TryRead(number, "1.5", out _, out SettingProblem? fraction)).IsFalse();
        await Assert.That(_TryRead(numbers, "[1, \"a\"]", out _, out SettingProblem? mixed)).IsFalse();
        await Assert.That(_TryRead(number, "{\"a\":1}", out _, out SettingProblem? obj)).IsFalse();
        await Assert.That(_TryRead(number, "null", out _, out SettingProblem? nothing)).IsFalse();

        await Assert.That(text?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(fraction?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(mixed?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(obj?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(nothing?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(obj?.Message).IsEqualTo("expected I64, got JSON object");
    }

    [Test]
    public async Task ValuesOutsideTheBoundsUseTheTextCodecMessages()
    {
        Setting port = Setting.U64("t.port", "Port", "t", 40001, 1, 65535);

        await Assert.That(_TryRead(port, "70000", out _, out SettingProblem? above)).IsFalse();
        await Assert.That(_TryRead(port, "0", out _, out SettingProblem? below)).IsFalse();

        await Assert.That(above?.Kind).IsEqualTo(SettingProblemKind.AboveMaximum);
        await Assert.That(above?.Message).IsEqualTo("allowed 1 – 65535");
        await Assert.That(below?.Kind).IsEqualTo(SettingProblemKind.BelowMinimum);
    }

    [Test]
    public async Task AnUndefinedElementIsRefusedAndNullArgumentsThrow()
    {
        Setting setting = Setting.Bool("t.bool", "Bool", "t", false);

        bool read = SettingValueJson.TryRead(setting, default, out _, out SettingProblem? problem);

        await Assert.That(read).IsFalse();
        await Assert.That(problem?.Kind).IsEqualTo(SettingProblemKind.WrongType);
        await Assert.That(() => SettingValueJson.TryRead(null!, default, out _, out _)).Throws<ArgumentNullException>(); // null! is the input under test
    }

    #endregion

    #region Persistence

    [Test]
    public async Task TheManagerStillLoadsAndSavesTheSameFiles()
    {
        using SettingsTempFolder folder = new();
        folder.Write("t.json", """{"t.enum":{"name":"high","value":1},"t.bytes":"AQL/","t.u64s":[1,"2"]}""");
        using SettingsManager manager = new(folder.Path);
        manager.RegisterSetting(Setting.Enum("t.enum", "Enum", "t", 0, [("low", 0UL), ("high", 1UL)]));
        manager.RegisterSetting(Setting.Bytes("t.bytes", "Bytes", "t", []));
        manager.RegisterSetting(Setting.U64Array("t.u64s", "U64s", "t", []));

        IReadOnlyList<SettingsLoadWarning> warnings = manager.Load();
        manager.Save();

        await Assert.That(warnings).IsEmpty();
        JsonObject saved = folder.ReadJson("t.json");
        await Assert.That(saved["t.enum"]?["name"]?.GetValue<string>()).IsEqualTo("high");
        await Assert.That(saved["t.bytes"]?.GetValue<string>()).IsEqualTo("AQL/");
        await Assert.That(saved["t.u64s"]?.ToJsonString()).IsEqualTo("[1,2]");
    }

    #endregion
}
