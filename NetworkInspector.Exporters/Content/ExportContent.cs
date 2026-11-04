// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// What an exporter writes: the shape, the selected fields, and the optional data per packet and per field.
/// Immutable record; validate it with the exporter's <c>WithContent</c>, which refuses members the format cannot write.
/// </summary>
/// <remarks>
/// One content type for every field exporter, so a caller configures CSV and DuckDB the same way. Each exporter exposes
/// <c>DefaultContent</c>, the content it writes when <c>WithContent</c> is not called (CSV exposes <c>ContentTemplate</c>, the
/// starting point for <c>WithContent</c>, because it writes packet rows without it). Every optional member costs work
/// only when set: a cleared text flag means the dissectors do not even build the text in a tree-free parse (the exporter's
/// observer does not ask for display text).
/// </remarks>
public sealed record ExportContent
{
    /// <summary>The shape; default <see cref="ExportShape.Hierarchy"/> (every field with its tree position).</summary>
    public ExportShape Shape { get; init; } = ExportShape.Hierarchy;

    /// <summary>The fields for <see cref="ExportShape.SelectedFields"/>; must be <see langword="null"/> for the other shapes.</summary>
    public FieldSelection? Fields { get; init; }

    /// <summary>Write the packet summary line (<c>Packet.Info</c>; also available in a tree-free parse).</summary>
    /// <remarks>
    /// The one exception to "the format writes what the member asks": the Text exporter has no info line (its tree shows the info as
    /// the <c>packet.info</c> field line, its flat shapes write none), so both values give the same Text output and
    /// <c>WithContent</c> does not refuse it; refusing would break its default content, which has <see cref="Info"/> set.
    /// </remarks>
    public bool Info { get; init; } = true;

    /// <summary>
    /// Write the raw frame bytes of every packet; off by default. The field tree is the leading content and already holds the
    /// parsed bytes, so the frame copy is additional and only written on request. Every packet exporter supports it: DuckDB,
    /// Parquet, and PBF Columnar as a binary column, PBF Standard as a packet field, JSON as a Base64 string, Text as a hex
    /// dump, and CSV through <see cref="Csv.CsvColumnKind.FrameBytes"/> (CSV takes it as a column, not through this member).
    /// </summary>
    /// <remarks>
    /// Perf: the default is false (no initializer, CA1805), so the batch copy of every frame is skipped for an export that does
    /// not ask for it; that copy is the largest single cost of a default export. Related: <see cref="Info"/> (the other
    /// packet-level member).
    /// Sync: the plugin option <c>frames</c> in <c>ExportContentOptions.RegisterContent</c> (NetworkInspector.BuiltInPlugins),
    /// which takes its default from this record, and the README option tables.
    /// </remarks>
    public bool FrameBytes { get; init; }

    /// <summary>
    /// Write the additional packet buffers of every packet (buffer index 1 to 255: bytes that protocols attached to the packet,
    /// such as reassembled IPv4 datagrams, TCP PDUs, decoded HTTP bodies, and unmasked or inflated WebSocket payloads); off by
    /// default. Each record is the buffer index and its bytes; a packet without additional buffers has no record. Buffer 0 is
    /// the frame and is written by <see cref="FrameBytes"/>. Only the packet exporters offer it (DuckDB, Parquet, PBF Standard,
    /// PBF Columnar, JSON, Text, and CSV through <see cref="Csv.CsvColumnKind.Buffers"/>); the frame exporters (PCAPNG, BLF,
    /// ASC) have no such option.
    /// </summary>
    /// <remarks>
    /// Perf: the default is false (no initializer, CA1805), so the batch copy of the buffers is skipped for an export that does
    /// not ask for it. Security: buffers can hold decoded payloads that the frame does not show in clear (an inflated HTTP
    /// body, an unmasked WebSocket payload), and a reassembled volume can be many times the size of the frames; the member is
    /// opt-in for both reasons, and the batch limits bound the memory the copies take. Related: <see cref="FrameBytes"/> (the
    /// other optional packet bytes member). CSV takes the buffers as a column, not through this member.
    /// Sync: the plugin option <c>buffers</c> in <c>ExportContentOptions.RegisterContent</c> (NetworkInspector.BuiltInPlugins),
    /// which takes its default from this record, and the README option tables.
    /// </remarks>
    public bool Buffers { get; init; }

    /// <summary>Write each field's custom text.</summary>
    public bool CustomText { get; init; } = true;

    /// <summary>Write each value's custom representation.</summary>
    public bool CustomRepresentation { get; init; } = true;

    /// <summary>Checks the shape/fields combination that every exporter requires.</summary>
    /// <param name="message">The problem when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the combination is valid.</returns>
    public bool TryValidateShape([NotNullWhen(false)] out string? message)
    {
        // The shape decides the parse demand; a selection without the SelectedFields shape (or the other way round) would
        // demand a different parse than the output promises, so the mismatch is refused instead of guessed.
        if (!Enum.IsDefined(Shape))
        {
            message = Invariant($"Unknown export shape {(int)Shape}.");
            return false;
        }

        if (Shape == ExportShape.SelectedFields && Fields is null)
        {
            message = "The shape SelectedFields needs a field selection.";
            return false;
        }

        if (Shape != ExportShape.SelectedFields && Fields is not null)
        {
            message = Invariant($"A field selection is only valid with the shape SelectedFields, not {Shape}.");
            return false;
        }

        message = null;
        return true;
    }
}
