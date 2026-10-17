// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>Outcome of <see cref="InterfaceChannelConverter.TryGetChannel"/>.</summary>
internal enum InterfaceChannelLookup
{
    /// <summary>No channel hint on the interface; the exporter uses its default channel.</summary>
    NotFound,

    /// <summary>A channel was resolved.</summary>
    Found,

    /// <summary>A channel property exists but its value is not an integer.</summary>
    Invalid,
}

/// <summary>
/// Shared conversion helpers for opaque interface-property channel values.
/// Used by frame exporters (ASC, BLF) so channel lookup stays exception-free and both Vector exporters resolve the
/// same channel for the same interface. Exporters cache the result per interface, so this runs once per interface.
/// </summary>
internal static class InterfaceChannelConverter
{
    /// <summary>
    /// Resolves the Vector channel of an interface: <see cref="BlfInterfacePropertyKeys.Channel"/> first (BLF sources),
    /// then <see cref="AscInterfacePropertyKeys.Channel"/> (ASC sources), then a trailing number of at least 1 after a
    /// space in the interface name (<c>"CAN 2"</c> → 2).
    /// </summary>
    /// <param name="info">Interface to inspect.</param>
    /// <param name="channel">Resolved channel when the result is <see cref="InterfaceChannelLookup.Found"/>; else 0.</param>
    /// <param name="invalidValue">The offending property value when the result is <see cref="InterfaceChannelLookup.Invalid"/>.</param>
    internal static InterfaceChannelLookup TryGetChannel(FrameInterfaceInfo info, out int channel, out object? invalidValue)
    {
        ArgumentNullException.ThrowIfNull(info);
        invalidValue = null;
        channel = 0;

        foreach (string key in (ReadOnlySpan<string>)[BlfInterfacePropertyKeys.Channel, AscInterfacePropertyKeys.Channel])
        {
            if (!info.Properties.TryGetValue(key, out object? value))
            {
                continue;
            }

            if (TryConvertToInt32(value, out channel))
            {
                return InterfaceChannelLookup.Found;
            }

            invalidValue = value;
            return InterfaceChannelLookup.Invalid;
        }

        return _TryTrailingNumber(info.UiName, out channel)
            ? InterfaceChannelLookup.Found
            : InterfaceChannelLookup.NotFound;
    }

    /// <summary>
    /// Parses <c>"... N"</c>: digits after the last space, value at least 1. <c>"eth0"</c> or <c>"Interface 0"</c> give no
    /// channel, so captures from other buses keep the exporter's default instead of channel 0.
    /// </summary>
    private static bool _TryTrailingNumber(string name, out int channel)
    {
        channel = 0;
        int space = name.LastIndexOf(' ');
        return space >= 0
            && space < name.Length - 1
            && int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out channel)
            && channel >= 1;
    }

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to an <see cref="int"/> without exception-based control flow.
    /// Accepts integral numeric types and parseable strings.
    /// </summary>
    internal static bool TryConvertToInt32(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                result = (int)l;
                return true;
            case uint u when u <= (uint)int.MaxValue:
                result = (int)u;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case string str when int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to a <see cref="ushort"/> without exception-based control flow.
    /// </summary>
    internal static bool TryConvertToUInt16(object? value, out ushort result)
    {
        if (!TryConvertToInt32(value, out int i) || i < 0 || i > ushort.MaxValue)
        {
            result = 0;
            return false;
        }

        result = (ushort)i;
        return true;
    }
}
