// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters.Text;

/// <summary>
/// Controls how much detail the <see cref="TextExporter"/> includes in its output.
/// </summary>
public enum TextDetailLevel : byte
{
    /// <summary>
    /// Only protocol container fields (<see cref="FieldType.None"/>) are shown.
    /// Value fields are omitted. Provides a structural protocol overview similar to
    /// Wireshark's "Protocol Summary" view.
    /// </summary>
    Summary = 0,

    /// <summary>
    /// All fields are shown except raw byte buffers (<see cref="FieldType.Bytes"/>).
    /// Long string values are truncated at the configured maximum length.
    /// This is the default level.
    /// </summary>
    Standard = 1,

    /// <summary>
    /// All fields including raw byte buffers are shown.
    /// Byte fields are rendered as space-separated lowercase hex.
    /// All values are still subject to the configured maximum text length.
    /// </summary>
    Full = 2,
}
