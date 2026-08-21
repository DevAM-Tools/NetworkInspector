// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>Compile-time validation coverage for <see cref="SignalMessageCompiler"/>.</summary>
internal sealed class SignalMessageCompilerTests
{
    [Test]
    public async Task CompileMessages_NullConfig_Throws()
    {
        List<SettingsLoadWarning> warnings = [];
        await Assert.That(() => SignalMessageCompiler.CompileMessages(
            null!,
            new SignalMessageCompileSettings(false, false, 8),
            warnings)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CompileMessages_NullWarnings_Throws()
    {
        await Assert.That(() => SignalMessageCompiler.CompileMessages(
            new(),
            new SignalMessageCompileSettings(false, false, 8),
            null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CompileMessages_MaxEnumValuesNotPositive_Throws()
    {
        List<SettingsLoadWarning> warnings = [];
        await Assert.That(() => SignalMessageCompiler.CompileMessages(
            new(),
            new SignalMessageCompileSettings(false, false, 0),
            warnings)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CompileMessages_EmptyMessages_ReturnsEmpty()
    {
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] result = SignalMessageCompiler.CompileMessages(
            new() { Messages = [] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(result.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_MissingName_AddsWarning()
    {
        await Assert.That(await _WarningFor(_Msg(name: "", ui: "U"))).Contains("name is required");
    }

    [Test]
    public async Task CompileMessages_MissingUiName_AddsWarning()
    {
        await Assert.That(await _WarningFor(_Msg(name: "m", ui: " "))).Contains("ui_name is required");
    }

    [Test]
    public async Task CompileMessages_CopiesSignalNameAndUiNameVerbatim()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].Name = "already.qualified.rpm";
        msg.Signals[0].UiName = "Engine RPM";
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [msg] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled.Length).IsEqualTo(1);
        await Assert.That(compiled[0].StaticSignals[0].Name).IsEqualTo("already.qualified.rpm");
        await Assert.That(compiled[0].StaticSignals[0].UiName).IsEqualTo("Engine RPM");
        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_ByteLengthBelowOne_AddsWarning()
    {
        await Assert.That(await _WarningFor(_Msg(name: "m", ui: "M", byteLength: 0))).Contains("byte_length");
    }

    [Test]
    public async Task CompileMessages_NullByteOrder_AddsWarningWithoutThrowing()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].ByteOrder = null!;
        SignalMessageConfig good = _Msg(name: "ok", ui: "Ok", port: 2);
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [msg, good] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled.Length).IsEqualTo(2);
        await Assert.That(compiled.Any(m => m.Name == "m" && m.StaticSignals.Length == 0)).IsTrue();
        await Assert.That(compiled.Any(m => m.Name == "ok" && m.StaticSignals.Length == 1)).IsTrue();
        await Assert.That(warnings.Any(w => w.Message.Contains("byte_order", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task CompileMessages_UnsupportedByteOrder_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].ByteOrder = "middle_endian";
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "unsupported byte_order");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_StartBitOutOfRange_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].StartBit = -1;
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "start_bit");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_BitLengthOutOfRange_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].BitLength = 65;
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "bit_length");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_MissingSignalName_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].Name = "";
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "signal name");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_InvalidValueNamesKey_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].BitLength = 8;
        msg.Signals[0].ValueNames = new Dictionary<string, string> { ["nope"] = "X" };
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "invalid value_names key");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_EnumKeyExceedsBitLength_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].BitLength = 2;
        msg.Signals[0].ValueNames = new Dictionary<string, string> { ["8"] = "X" };
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "exceeds max raw");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_EmptyEnumName_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].BitLength = 8;
        msg.Signals[0].ValueNames = new Dictionary<string, string> { ["1"] = "" };
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "must be non-empty");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_ByteLengthLessThanRequired_AddsWarning()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M", byteLength: 1);
        await Assert.That(await _SkippedMessageWarning(msg)).Contains("RequiredByteLength");
    }

    [Test]
    public async Task CompileMessages_DuplicateMuxValue_KeepsFirstGroup()
    {
        SignalMessageConfig msg = _MuxMsg();
        msg.MuxGroups =
        [
            new() { MuxValue = 0, Signals = [_Le8("a")] },
            new() { MuxValue = 0, Signals = [_Le8("b")] },
        ];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "duplicate mux_value");
        await Assert.That(compiled.MuxGroups.Length).IsEqualTo(1);
        await Assert.That(compiled.MuxGroups[0].Signals[0].Name).IsEqualTo("a");
    }

    [Test]
    public async Task CompileMessages_MuxValueExceedsBitLength_SkipsGroup_KeepsMux()
    {
        SignalMessageConfig msg = _MuxMsg();
        msg.MuxGroups = [new() { MuxValue = 256, Signals = [_Le8("a")] }];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "exceeds max raw");
        await Assert.That(compiled.MuxSignal).IsNotNull();
        await Assert.That(compiled.MuxGroups.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_MuxMissingName_SkipsMux_KeepsMessage()
    {
        SignalMessageConfig msg = _MuxMsg();
        msg.MuxSignal!.Name = "";
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "mux_signal");
        await Assert.That(compiled.MuxSignal).IsNull();
    }

    [Test]
    public async Task CompileMessages_MuxBadBitLayout_SkipsMux_KeepsMessage()
    {
        SignalMessageConfig msg = _MuxMsg();
        msg.MuxSignal!.BitLength = 0;
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "mux_signal");
        await Assert.That(compiled.MuxSignal).IsNull();
    }

    [Test]
    public async Task CompileMessages_MuxGroupSignalInvalid_SkipsSignal_KeepsMux()
    {
        SignalMessageConfig msg = _MuxMsg();
        msg.MuxGroups = [new() { MuxValue = 0, Signals = [_Le8("")] }];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "signal name");
        await Assert.That(compiled.MuxSignal).IsNotNull();
        await Assert.That(compiled.MuxGroups.Length).IsEqualTo(1);
        await Assert.That(compiled.MuxGroups[0].Signals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_KeepsSiblingSignalWhenOneIsInvalid()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals = [_Le8("ok"), _Le8("")];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "signal name");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(1);
        await Assert.That(compiled.StaticSignals[0].Name).IsEqualTo("ok");
    }

    [Test]
    public async Task CompileMessages_DuplicateSignalName_KeepsFirst()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        SignalFieldConfig first = _Le8("m.s");
        first.StartBit = 0;
        SignalFieldConfig second = _Le8("m.s");
        second.StartBit = 8;
        msg.Signals = [first, second];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "collides");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(1);
        await Assert.That(compiled.StaticSignals[0].StartBit).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task CompileMessages_SignalNameEqualsMessageName_SkipsSignal()
    {
        SignalMessageConfig msg = _Msg(name: "collide", ui: "C");
        msg.Signals[0].Name = "collide";
        msg.Signals[0].BitLength = 8;
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "collides");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_SharedSignalNameAcrossMessages_SkipsSecondSignal()
    {
        SignalMessageConfig first = _Msg(name: "m1", ui: "M1");
        first.Signals[0].Name = "shared.x";
        first.Signals[0].BitLength = 8;
        SignalMessageConfig second = _Msg(name: "m2", ui: "M2", port: 2);
        second.Signals[0].Name = "shared.x";
        second.Signals[0].BitLength = 8;
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [first, second] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled.Length).IsEqualTo(2);
        await Assert.That(compiled[0].StaticSignals.Length).IsEqualTo(1);
        await Assert.That(compiled[1].StaticSignals.Length).IsEqualTo(0);
        await Assert.That(warnings.Any(w => w.Message.Contains("collides", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task CompileMessages_MuxGroupsWithoutMuxSignal_WarnsAndKeepsStatics()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.MuxGroups = [new() { MuxValue = 0, Signals = [_Le8("a")] }];
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "mux_groups ignored");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(1);
        await Assert.That(compiled.MuxSignal).IsNull();
        await Assert.That(compiled.MuxGroups.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_InvalidSignalName_SkipsSignal_KeepsMessage()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Signals[0].Name = "1bad";
        msg.Signals[0].BitLength = 8;
        CompiledSignalMessage compiled = await _CompiledWithWarning(msg, "invalid name");
        await Assert.That(compiled.StaticSignals.Length).IsEqualTo(0);
    }

    [Test]
    public async Task CompileMessages_WhitespaceDescription_UsesDefault()
    {
        SignalMessageConfig msg = _Msg(name: "m", ui: "M");
        msg.Description = "  ";
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [msg] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled[0].Description).IsEqualTo(SignalMessageCompiler.DefaultMessageDescription);
    }

    private static async Task<string> _WarningFor(SignalMessageConfig msg)
        => await _SkippedMessageWarning(msg);

    private static async Task<string> _SkippedMessageWarning(SignalMessageConfig msg)
    {
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [msg] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled.Length).IsEqualTo(0);
        await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(1);
        return warnings[0].Message;
    }

    private static async Task<CompiledSignalMessage> _CompiledWithWarning(SignalMessageConfig msg, string warningSubstring)
    {
        List<SettingsLoadWarning> warnings = [];
        CompiledSignalMessage[] compiled = SignalMessageCompiler.CompileMessages(
            new() { Messages = [msg] },
            new SignalMessageCompileSettings(false, false, 8),
            warnings);
        await Assert.That(compiled.Length).IsEqualTo(1);
        await Assert.That(warnings.Any(w => w.Message.Contains(warningSubstring, StringComparison.Ordinal))).IsTrue();
        return compiled[0];
    }

    private static SignalFieldConfig _Le8(string name) => new()
    {
        Name = name,
        UiName = name.Length == 0 ? "U" : name,
        StartBit = 8,
        BitLength = 8,
        ByteOrder = "little_endian",
    };

    private static SignalMessageConfig _Msg(string name, string ui, int byteLength = 2, int port = 1) => new()
    {
        Name = name,
        UiName = ui,
        ByteLength = byteLength,
        DispatchBindings = [new() { Table = "udp.port", Key = (ulong)port }],
        Signals =
        [
            new()
            {
                Name = "s",
                UiName = "S",
                StartBit = 0,
                BitLength = 16,
                ByteOrder = "little_endian",
            },
        ],
    };

    private static SignalMessageConfig _MuxMsg() => new()
    {
        Name = "muxed",
        UiName = "Muxed",
        ByteLength = 2,
        MuxSignal = new()
        {
            Name = "mux",
            UiName = "Mux",
            StartBit = 0,
            BitLength = 8,
            ByteOrder = "little_endian",
        },
        MuxGroups = [new() { MuxValue = 0, Signals = [_Le8("a")] }],
    };
}
