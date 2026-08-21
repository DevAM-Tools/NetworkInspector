// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using ParserDispatchBinding = NetworkInspector.Protocols.SignalMessage.DispatchBinding;

namespace NetworkInspector.Protocols.Tests.Infrastructure.Bridges;

/// <summary>
/// Mirrors a <see cref="SignalMessageLayout"/> into the parser JSON model so the Stack and tshark
/// share the same semantic definition.
/// </summary>
internal static class SignalMessageConfigBridge
{
    internal static SignalMessagesConfig FromLayout(SignalMessageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        SignalFieldConfig[] defs = _ConvertSignals(layout.Name, layout.Signals);

        ImmutableArray<FrameDispatchBinding> bindings = layout.DispatchBindings;
        ParserDispatchBinding[] dispatchBindings;
        if (bindings.IsDefault || bindings.Length == 0)
        {
            dispatchBindings = [];
        }
        else
        {
            dispatchBindings = new ParserDispatchBinding[bindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                FrameDispatchBinding binding = bindings[i];
                dispatchBindings[i] = new ParserDispatchBinding { Table = binding.Table, Key = binding.Key };
            }
        }

        MuxSignalConfig? muxSignal = null;
        MuxGroupConfig[] muxGroups = [];
        if (layout.Mux is not null)
        {
            MuxSpec mux = layout.Mux.Value;
            string muxUi = string.IsNullOrEmpty(mux.UiName) ? mux.Name : mux.UiName;
            muxSignal = new MuxSignalConfig
            {
                Name = _QualifiedFieldName(layout.Name, mux.Name),
                UiName = muxUi,
                StartBit = mux.StartBit,
                BitLength = mux.BitLength,
                ByteOrder = mux.Endian == SignalEndian.Big ? "big_endian" : "little_endian",
            };

            ImmutableArray<MuxGroupSpec> groups = layout.MuxGroups;
            if (!groups.IsDefault && groups.Length > 0)
            {
                muxGroups = new MuxGroupConfig[groups.Length];
                for (int g = 0; g < groups.Length; g++)
                {
                    MuxGroupSpec group = groups[g];
                    muxGroups[g] = new MuxGroupConfig
                    {
                        MuxValue = group.MuxValue,
                        Signals = _ConvertSignals(layout.Name, group.Signals),
                    };
                }
            }
        }
        else if (!(layout.MuxGroups.IsDefault || layout.MuxGroups.Length == 0))
        {
            throw new InvalidOperationException(
                "MuxGroups are present but Mux selector is null; layout is inconsistent.");
        }

        string uiName = string.IsNullOrEmpty(layout.UiName) ? layout.Name : layout.UiName;

        return new SignalMessagesConfig
        {
            Messages =
            [
                new SignalMessageConfig
                {
                    Name = layout.Name,
                    UiName = uiName,
                    ByteLength = layout.ByteLength,
                    DispatchBindings = dispatchBindings,
                    Signals = defs,
                    MuxSignal = muxSignal,
                    MuxGroups = muxGroups,
                },
            ],
        };
    }

    internal static string SerializeJson(SignalMessageLayout layout) =>
        JsonSerializer.Serialize(
            FromLayout(layout),
            SignalMessagesConfigContext.Default.SignalMessagesConfig);

    /// <summary>Qualifies a layout-local encoder key as the JSON field name.</summary>
    private static string _QualifiedFieldName(string messageName, string signalName)
        => $"{messageName}.{signalName}";

    /// <summary>Converts a signal array, writing JSON names in target form.</summary>
    private static SignalFieldConfig[] _ConvertSignals(string messageName, ImmutableArray<SignalSpec> signals)
    {
        if (signals.IsDefault || signals.Length == 0)
        {
            return [];
        }

        SignalFieldConfig[] defs = new SignalFieldConfig[signals.Length];
        for (int i = 0; i < signals.Length; i++)
        {
            defs[i] = _ConvertSignal(messageName, signals[i]);
        }

        return defs;
    }

    /// <summary>Converts one layout signal; JSON <c>name</c> is <c>{message}.{signal}</c>.</summary>
    private static SignalFieldConfig _ConvertSignal(string messageName, in SignalSpec s)
    {
        Dictionary<string, string>? valueNames = null;
        if (s.ValueNames is not null && s.ValueNames.Count > 0)
        {
            valueNames = new Dictionary<string, string>(s.ValueNames.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<ulong, string> kvp in s.ValueNames)
            {
                valueNames[kvp.Key.ToString(CultureInfo.InvariantCulture)] = kvp.Value;
            }
        }

        string uiName = string.IsNullOrEmpty(s.UiName) ? s.Name : s.UiName;

        return new SignalFieldConfig
        {
            Name = _QualifiedFieldName(messageName, s.Name),
            UiName = uiName,
            StartBit = s.StartBit,
            BitLength = s.BitLength,
            ByteOrder = s.Endian == SignalEndian.Big ? "big_endian" : "little_endian",
            Factor = s.Factor,
            Offset = s.Offset,
            Unit = s.Unit ?? string.Empty,
            ValueNames = valueNames,
        };
    }
}
