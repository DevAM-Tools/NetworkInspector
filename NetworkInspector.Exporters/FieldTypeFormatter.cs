// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// Shared formatter that converts a <see cref="FieldType"/> into a stable
/// textual name for human-oriented exporters (JSON Pretty/Array, text output).
/// <para>
/// Numeric enum values are reserved for compact, machine-oriented encodings
/// such as <see cref="Json.JsonExportFormat.Compact"/> and PBF.
/// </para>
/// </summary>
internal static class FieldTypeFormatter
{
    /// <summary>
    /// Returns the canonical textual name of <paramref name="type"/>.
    /// Matches the <see cref="FieldType"/> enum member names.
    /// </summary>
    /// <param name="type">The field type to format.</param>
    /// <returns>The enum member name, or <c>"Unknown"</c> for undefined values.</returns>
    internal static ReadOnlySpan<char> GetName(FieldType type) => type switch
    {
        FieldType.None => "None",
        FieldType.I64 => "I64",
        FieldType.U64 => "U64",
        FieldType.F64 => "F64",
        FieldType.String => "String",
        FieldType.Bytes => "Bytes",
        FieldType.MacAddress => "MacAddress",
        FieldType.IPv4Address => "IPv4Address",
        FieldType.IPv6Address => "IPv6Address",
        FieldType.Eui64 => "Eui64",
        FieldType.Uuid => "Uuid",
        FieldType.Timestamp => "Timestamp",
        FieldType.Bool => "Bool",
        _ => "Unknown",
    };
}
