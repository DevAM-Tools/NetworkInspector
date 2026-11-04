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
/// <para>
/// <b>Thread safety:</b> Stateless; safe to call from multiple threads.
/// </para>
/// </summary>
internal static class InterfaceChannelConverter
{
    #region Channel lookup

    /// <summary>
    /// Resolves the Vector channel of an interface: <see cref="BlfInterfacePropertyKeys.Channel"/> first (BLF sources),
    /// then <see cref="AscInterfacePropertyKeys.Channel"/> (ASC sources), then a trailing number of at least 1 after a
    /// space in the interface name (<c>"CAN 2"</c> → 2).
    /// </summary>
    /// <param name="info">Interface to inspect.</param>
    /// <param name="channel">Resolved channel when the result is <see cref="InterfaceChannelLookup.Found"/>; else 0.</param>
    /// <param name="invalidValue">The offending property value when the result is <see cref="InterfaceChannelLookup.Invalid"/>.</param>
    /// <returns>
    /// <see cref="InterfaceChannelLookup.Found"/>, <see cref="InterfaceChannelLookup.NotFound"/>, or
    /// <see cref="InterfaceChannelLookup.Invalid"/>. A returned channel is not range-checked: it can be negative or larger
    /// than any bus channel limit, and the calling exporter decides what it accepts.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is <see langword="null"/>.</exception>
    internal static InterfaceChannelLookup TryGetChannel(FrameInterfaceInfo info, out int channel, out object? invalidValue)
    {
        ArgumentNullException.ThrowIfNull(info);
        invalidValue = null;
        channel = 0;

        // Explicit properties win over the name, in priority order: BLF channel, then ASC channel. A property that is
        // present but not convertible is reported as Invalid at once and does not fall through to the next source, so a
        // broken explicit value is never silently replaced by a guess from the name.
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

        // No channel property at all: the interface name may still carry the channel ("CAN 2").
        return _TryTrailingNumber(info.UiName, out channel)
            ? InterfaceChannelLookup.Found
            : InterfaceChannelLookup.NotFound;
    }

    /// <summary>
    /// Parses <c>"... N"</c>: digits after the last space, value at least 1. <c>"eth0"</c> or <c>"Interface 0"</c> give no
    /// channel, so captures from other buses keep the exporter's default instead of channel 0.
    /// </summary>
    /// <param name="name">The interface display name.</param>
    /// <param name="channel">The parsed channel when the result is <see langword="true"/>; otherwise 0.</param>
    /// <returns><see langword="true"/> when the name ends in a space and a number of at least 1.</returns>
    private static bool _TryTrailingNumber(string name, out int channel)
    {
        channel = 0;
        int space = name.LastIndexOf(' ');
        return space >= 0
            && space < name.Length - 1
            && int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out channel)
            && channel >= 1;
    }

    #endregion

    #region Integer conversion

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to an <see cref="int"/> without exception-based control flow.
    /// Accepts the signed and unsigned integral types up to 32 bits, a <see cref="long"/> or <see cref="uint"/> inside the
    /// <see cref="int"/> range, and strings that parse as an invariant-culture integer (surrounding white space and a sign
    /// are allowed). Other types (for example <see cref="ulong"/> or floating point) are rejected, not truncated.
    /// </summary>
    /// <param name="value">Property value of unknown type.</param>
    /// <param name="result">The converted value, or 0 when the conversion fails.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a supported type holding an <see cref="int"/>-range value.</returns>
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
    /// <param name="value">Property value of unknown type; see <see cref="TryConvertToInt32"/> for the accepted types.</param>
    /// <param name="result">The converted value, or 0 when the conversion fails.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> converts to an integer from 0 to <see cref="ushort.MaxValue"/>.</returns>
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

    #endregion
}
