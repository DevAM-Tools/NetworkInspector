// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Shared conversion helpers for opaque interface-property channel values.
/// Used by frame exporters (ASC, BLF) so channel lookup stays exception-free on the hot path.
/// </summary>
internal static class InterfaceChannelConverter
{
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
