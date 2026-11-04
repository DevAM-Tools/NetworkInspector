// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Text;

/// <summary>
/// Controls how much detail the <see cref="TextExporter"/> includes in its output.
/// </summary>
public enum TextDetailLevel : byte
{
    /// <summary>
    /// Only containers are shown: groups without a value (<see cref="FieldType.None"/>) and protocol layers (a field with
    /// children that carries its raw bytes as a <see cref="FieldType.Bytes"/> value). The level only selects which fields get a
    /// line; each line is the display line of the rule (a custom text is the whole line, otherwise the label and the value as
    /// text, a protocol layer without a custom text with the upper-case hex of its bytes). Value fields are omitted. Provides a
    /// structural protocol overview: one line per top-level container. Only the direct children of the packet root are visited:
    /// the exporter does not descend into the children of a container at this level, so containers nested inside another
    /// container are not listed.
    /// </summary>
    Summary = 0,

    /// <summary>
    /// All fields are shown except raw byte buffers: a <see cref="FieldType.Bytes"/> field without children is left out. A
    /// protocol layer (a <see cref="FieldType.Bytes"/> field with children) is structure and stays visible. The level only selects
    /// which fields get a line; each line is the display line of the rule.
    /// Long string values are truncated at the configured maximum length.
    /// This is the default level.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// All fields including raw byte buffers are shown.
    /// Byte fields, protocol layers included, are rendered as upper-case hex pairs separated by one space (as many whole bytes as
    /// fit the maximum text length are shown) as part of the display line; a custom text replaces the whole line when a dissector
    /// set one.
    /// All values are still subject to the configured maximum text length.
    /// </summary>
    Full = 2,
}
