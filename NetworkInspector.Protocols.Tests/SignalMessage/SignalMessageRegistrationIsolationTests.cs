// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.SignalMessage;

/// <summary>
/// Isolation: a failed signal-message compile/registration must not block sibling messages.
/// </summary>
internal sealed class SignalMessageRegistrationIsolationTests
{
    [Test]
    public async Task Register_SkipsInvalidMessage_RegistersValidSibling()
    {
        // bad_msg: byte_length 1 < RequiredByteLength 2 for a 16-bit LE signal at bit 0.
        // good_msg: valid 2-byte layout — must still register.
        string json = """
            {
              "messages": [
                {
                  "name": "bad_msg",
                  "ui_name": "Bad",
                  "byte_length": 1,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17201 }],
                  "signals": [{
                    "name": "bad_msg.a",
                    "ui_name": "A",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                },
                {
                  "name": "good_msg",
                  "ui_name": "Good",
                  "byte_length": 2,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17202 }],
                  "signals": [{
                    "name": "good_msg.b",
                    "ui_name": "B",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                }
              ]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_iso_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());

            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);

            await Assert.That(warnings.Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(warnings.Any(w => w.Message.Contains("bad_msg", StringComparison.Ordinal))).IsTrue();

            Stack stack = builder.Build();
            using (stack)
            {
                await Assert.That(stack.GetProtocolId("bad_msg").HasValue).IsFalse();
                await Assert.That(stack.GetProtocolId("good_msg").HasValue).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_DuplicateName_SkipsSecond_KeepsFirst()
    {
        string json = """
            {
              "messages": [
                {
                  "name": "dup_msg",
                  "ui_name": "First",
                  "byte_length": 2,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17211 }],
                  "signals": [{
                    "name": "dup_msg.s",
                    "ui_name": "S",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                },
                {
                  "name": "dup_msg",
                  "ui_name": "Second",
                  "byte_length": 2,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17212 }],
                  "signals": [{
                    "name": "dup_msg.t",
                    "ui_name": "T",
                    "start_bit": 0,
                    "bit_length": 16,
                    "byte_order": "little_endian"
                  }]
                }
              ]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_dup_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);

            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());

            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);

            await Assert.That(warnings.Any(w => w.Message.Contains("Duplicate", StringComparison.Ordinal))).IsTrue();

            Stack stack = builder.Build();
            using (stack)
            {
                ProtocolId? id = stack.GetProtocolId("dup_msg");
                await Assert.That(id.HasValue).IsTrue();
                // First definition wins (ui_name "First").
                ProtocolInfo? info = stack.GetProtocol(id!.Value);
                await Assert.That(info).IsNotNull();
                await Assert.That(info!.UiName).IsEqualTo("First");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_SkipsBadSignal_KeepsSiblingInSameMessage()
    {
        string json = """
            {
              "messages": [{
                "name": "partial_msg",
                "ui_name": "Partial",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17221 }],
                "signals": [
                  {
                    "name": "partial_msg.ok",
                    "ui_name": "Ok",
                    "start_bit": 0,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  },
                  {
                    "name": "",
                    "ui_name": "Bad",
                    "start_bit": 8,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }
                ]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_sib_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w => w.Message.Contains("Skipping signal", StringComparison.Ordinal))).IsTrue();
            Stack stack = builder.Build();
            using (stack)
            {
                await Assert.That(stack.GetProtocolId("partial_msg").HasValue).IsTrue();
                await Assert.That(stack.GetFieldId("partial_msg.ok").HasValue).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_DuplicateSignalName_KeepsFirst()
    {
        string json = """
            {
              "messages": [{
                "name": "dup_sig",
                "ui_name": "DupSig",
                "byte_length": 2,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17222 }],
                "signals": [
                  {
                    "name": "dup_sig.s",
                    "ui_name": "First",
                    "start_bit": 0,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  },
                  {
                    "name": "dup_sig.s",
                    "ui_name": "Second",
                    "start_bit": 8,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }
                ]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_dups_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w => w.Message.Contains("collides", StringComparison.Ordinal))).IsTrue();
            Stack stack = builder.Build();
            using (stack)
            {
                await Assert.That(stack.GetProtocolId("dup_sig").HasValue).IsTrue();
                await Assert.That(stack.GetFieldId("dup_sig.s").HasValue).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_SharedSignalNameAcrossMessages_SkipsSecondSignal()
    {
        string json = """
            {
              "messages": [
                {
                  "name": "share_a",
                  "ui_name": "A",
                  "byte_length": 1,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17223 }],
                  "signals": [{
                    "name": "shared.x",
                    "ui_name": "X",
                    "start_bit": 0,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }]
                },
                {
                  "name": "share_b",
                  "ui_name": "B",
                  "byte_length": 1,
                  "dispatch_bindings": [{ "table": "udp.port", "key": 17224 }],
                  "signals": [{
                    "name": "shared.x",
                    "ui_name": "X2",
                    "start_bit": 0,
                    "bit_length": 8,
                    "byte_order": "little_endian"
                  }]
                }
              ]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_share_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w => w.Message.Contains("collides", StringComparison.Ordinal))).IsTrue();
            Stack stack = builder.Build();
            using (stack)
            {
                await Assert.That(stack.GetProtocolId("share_a").HasValue).IsTrue();
                await Assert.That(stack.GetProtocolId("share_b").HasValue).IsTrue();
                await Assert.That(stack.GetFieldId("shared.x").HasValue).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Register_SignalNameEqualsMessageName_SkipsSignal()
    {
        string json = """
            {
              "messages": [{
                "name": "collide",
                "ui_name": "Collide",
                "byte_length": 1,
                "dispatch_bindings": [{ "table": "udp.port", "key": 17225 }],
                "signals": [{
                  "name": "collide",
                  "ui_name": "Same",
                  "start_bit": 0,
                  "bit_length": 8,
                  "byte_order": "little_endian"
                }]
              }]
            }
            """;

        string dir = Path.Combine(Path.GetTempPath(), "ni_spdu_eq_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "signal_message.json");
        try
        {
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
            using SettingsManager settings = new();
            settings.PreloadValue(SignalMessageRegistration.ConfigFileSetting, path);
            StackBuilder builder = new(settings, new FrameInterfaceRegistry());
            IReadOnlyList<SettingsLoadWarning> warnings = SignalMessageRegistration.Register(builder);
            await Assert.That(warnings.Any(w => w.Message.Contains("collides", StringComparison.Ordinal))).IsTrue();
            Stack stack = builder.Build();
            using (stack)
            {
                await Assert.That(stack.GetProtocolId("collide").HasValue).IsTrue();
                FieldId? containerId = stack.GetFieldId("collide");
                await Assert.That(containerId.HasValue).IsTrue();
                FieldInfo? container = stack.GetField(containerId!.Value);
                await Assert.That(container).IsNotNull();
                await Assert.That(container!.FieldType).IsEqualTo(FieldType.Bytes);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
