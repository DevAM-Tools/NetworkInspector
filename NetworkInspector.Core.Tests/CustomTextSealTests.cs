// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests the custom-text rule: text is final at append, a change before the seal stays possible (discouraged), and a change
/// after the seal is refused with an error that names the rule.
/// <para>
/// The protocol under test appends a field with text during the parse and a lazy container whose populator runs after the seal
/// (when the test reads the container's children). The populator tries all three text-changing methods on the already
/// appended field and records what happened, so a change that sneaks through or throws the wrong error is visible.
/// </para>
/// </summary>
internal sealed class CustomTextSealTests
{
    private const string _AppendedText = "set at append";

    [Test]
    public async Task ChangeAfterSealIsRefusedByAllThreeMethodsAndKeepsTheText()
    {
        (Stack stack, SealProbeProtocol protocol, ProtocolId protocolId) = _Build();
        using (stack)
        {
            Packet packet = _Parse(stack, protocolId);

            // The populator runs now, after the seal: reading the container's children materializes it.
            _ = packet.FieldCount(materialize: true);

            await Assert.That(protocol.SetError).IsNotNull();
            await Assert.That(protocol.AppendError).IsNotNull();
            await Assert.That(protocol.ClearError).IsNotNull();
            await Assert.That(protocol.SetError!).Contains("cannot change after the packet is sealed");
            await Assert.That(protocol.AppendError!).Contains("cannot change after the packet is sealed");
            await Assert.That(protocol.ClearError!).Contains("cannot change after the packet is sealed");

            // The text of the field is still the one set at append; no refused call changed it half way.
            await Assert.That(_FindText(packet, protocol.LazyFieldId)).IsEqualTo(_AppendedText);
        }
    }

    [Test]
    public async Task ChangeBeforeSealStillWorksWithEachMethod()
    {
        (Stack stack, SealProbeProtocol protocol, ProtocolId protocolId) = _Build();
        using (stack)
        {
            Packet packet = _Parse(stack, protocolId);

            // The parse changed the text of the second field with Set, Append, and Clear+Set before the seal.
            await Assert.That(_FindText(packet, protocol.ChangedFieldId)).IsEqualTo("replaced and extended");
        }
    }

    private static (Stack Stack, SealProbeProtocol Protocol, ProtocolId ProtocolId) _Build()
    {
        using SettingsManager settingsManager = new();
        StackBuilder builder = new(settingsManager, new FrameInterfaceRegistry());
        SealProbeProtocol protocol = new();
        ProtocolId protocolId = builder.RegisterProtocol(protocol);
        protocol.RegisterFields(builder, protocolId);
        return (builder.Build(), protocol, protocolId);
    }

    private static Packet _Parse(Stack stack, ProtocolId protocolId)
    {
        Frame frame = Frame.Create(
            1,
            Timestamp.FromSecs(1000),
            new byte[14],
            LinkType.Ethernet,
            FrameInterfaceId.Invalid,
            stack.FrameInterfaceRegistry).Value;
        ParseOptions options = new(firstProtocol: protocolId);
        if (!Packet.TryParse(0, stack, frame, in options, out Packet? packet, out ParseFailure failure))
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet!; // Invariant: TryParse returned true.
    }

    /// <summary>Returns the custom text of the first field with the id, searching the fully materialized tree.</summary>
    private static string? _FindText(Packet packet, FieldId fieldId) => _FindText(packet.RootField(), fieldId);

    private static string? _FindText(Field parent, FieldId fieldId)
    {
        foreach (Field child in parent.Children(materialize: true)) // materialize: true — the probe field sits below a lazy container
        {
            if (child.FieldId == fieldId)
            {
                return child.TryGetCustomText(out LazyString text)
                    ? text.AsString
                    : null;
            }

            string? deeper = _FindText(child, fieldId);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>
    /// Appends a field with text, changes the text of a second field before the seal, and appends a lazy container whose
    /// populator (after the seal) tries to change the text of the first field.
    /// </summary>
    private sealed class SealProbeProtocol : IProtocol
    {
        private FieldId _ContainerFieldId;
        public FieldId LazyFieldId { get; private set; }

        public string Name => "seal";
        public string UiName => "Seal probe";
        public FieldId ValueFieldId { get; private set; }
        public FieldId ChangedFieldId { get; private set; }
        public string? SetError { get; private set; }
        public string? AppendError { get; private set; }
        public string? ClearError { get; private set; }

        public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
        {
            _ContainerFieldId = builder.RegisterField(protocolId, "seal", "Seal probe", FieldType.None);
            ValueFieldId = builder.RegisterField(protocolId, "seal.value", "Value", FieldType.U64);
            ChangedFieldId = builder.RegisterField(protocolId, "seal.changed", "Changed", FieldType.U64);
            LazyFieldId = builder.RegisterField(protocolId, "seal.lazy", "Lazy", FieldType.None);
        }

        public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
        {
            MutField container = parentField.Append(_ContainerFieldId, FieldValue.None);
            _ = container.AppendWithCustomText(ValueFieldId, FieldValue.NewU64(1), new LazyString(_AppendedText));

            // Before the seal all three methods keep working (discouraged, but not refused).
            MutField changed = container.AppendWithCustomText(ChangedFieldId, FieldValue.NewU64(2), new LazyString("original"));
            changed.SetCustomText(new LazyString("replaced"));
            changed.AppendCustomText(new LazyString(" and extended"));
            MutField cleared = container.AppendWithCustomText(ChangedFieldId, FieldValue.NewU64(3), new LazyString("to be cleared"));
            cleared.ClearCustomText();

            // The populator runs when the container's children are read, which is after the seal.
            _ = container.AppendLazyWithCustomText(LazyFieldId, FieldValue.None, new LazyString(_AppendedText), (in MutField lazy) =>
            {
                SetError = _Try(in lazy, mode: 0);
                AppendError = _Try(in lazy, mode: 1);
                ClearError = _Try(in lazy, mode: 2);
                return 0;
            });
            return 14;
        }

        /// <summary>Calls one text-changing method and returns the message of the refusal, or null when it was accepted.</summary>
        private static string? _Try(in MutField field, int mode)
        {
            try
            {
                switch (mode)
                {
                    case 0:
                        field.SetCustomText(new LazyString("late"));
                        break;
                    case 1:
                        field.AppendCustomText(new LazyString(" late"));
                        break;
                    default:
                        field.ClearCustomText();
                        break;
                }

                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }
    }
}
