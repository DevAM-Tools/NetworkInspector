// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Text.Json;
using NetworkInspector.FrameBuilder;
using NetworkInspector.Protocols.SignalPdu;

namespace NetworkInspector.Protocols.Tests.Infrastructure.Bridges;

/// <summary>
/// Mirrors a <see cref="SignalPduLayout"/> into the parser JSON model so the Stack and tshark
/// share the same semantic definition.
/// </summary>
internal static class SignalPduConfigBridge
{
    internal static SignalPduConfig FromLayout(SignalPduLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (layout.Mux is not null || !(layout.MuxGroups.IsDefault || layout.MuxGroups.Length == 0))
        {
            throw new NotSupportedException("Mux bridging is not wired in this converter yet.");
        }

        SignalDefinition[] defs;
        ImmutableArray<SignalSpec> signals = layout.Signals;
        if (signals.IsDefault || signals.Length == 0)
        {
            defs = [];
        }
        else
        {
            defs = new SignalDefinition[signals.Length];
            for (int i = 0; i < signals.Length; i++)
            {
                defs[i] = ConvertSignal(signals[i]);
            }
        }

        ImmutableArray<DispatchBinding> regs = layout.RegisterAt;
        SignalPduRegistration[] registerAt;
        if (regs.IsDefault || regs.Length == 0)
        {
            registerAt = [];
        }
        else
        {
            registerAt = new SignalPduRegistration[regs.Length];
            for (int i = 0; i < regs.Length; i++)
            {
                DispatchBinding binding = regs[i];
                registerAt[i] = new SignalPduRegistration { Table = binding.Table, Key = binding.Key };
            }
        }

        return new SignalPduConfig
        {
            Pdus =
            [
                new SignalPduDefinition
                {
                    PduId = layout.PduId,
                    Name = layout.Name,
                    ByteLength = layout.ByteLength,
                    RegisterAt = registerAt,
                    Signals = defs,
                    MuxSignal = null,
                    MuxGroups = [],
                },
            ],
        };
    }

    internal static string SerializeJson(SignalPduLayout layout) =>
        JsonSerializer.Serialize(
            FromLayout(layout),
            SignalPduConfigContext.Default.SignalPduConfig);

    private static SignalDefinition ConvertSignal(in SignalSpec s)
    {
        Dictionary<string, string>? valueNames = null;
        if (s.ValueNames is not null && s.ValueNames.Count > 0)
        {
            valueNames = new Dictionary<string, string>(s.ValueNames.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<ulong, string> kvp in s.ValueNames)
            {
                valueNames[kvp.Key.ToString()] = kvp.Value;
            }
        }

        return new SignalDefinition
        {
            Name = s.Name,
            StartBit = s.StartBit,
            BitLength = s.BitLength,
            ByteOrder = s.Endian == SignalEndian.Big ? "big_endian" : "little_endian",
            DataType = s.Type switch
            {
                SignalType.Unsigned => "unsigned",
                SignalType.Signed => "signed",
                SignalType.F32 => "float32",
                SignalType.F64 => "float64",
                _ => throw new InvalidOperationException($"Unsupported signal type ordinal {(uint)s.Type}."),
            },
            Factor = s.Factor,
            Offset = s.Offset,
            Unit = s.Unit ?? string.Empty,
            ValueNames = valueNames,
        };
    }
}
