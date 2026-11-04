// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// Reads the optional texts of a field (the custom representation of its value and its custom text) for the exporters, with
/// presence: a text that no dissector set is <see langword="null"/>, a text that is set but empty is the empty string. A text that
/// the export content does not write is not read at all, because reading can complete a deferred text (which costs work and can
/// throw). Used by the columnar batch, the row exporters, and the sinks that write the texts.
/// <para>
/// <b>Thread safety:</b> Stateless; safe to call from multiple threads.
/// </para>
/// </summary>
internal static class OptionalTextReader
{
    #region Optional columns

    // Invariant: for a custom text or representation, null means absent (no dissector set one) and the empty string means set but
    // empty. Every sink relies on this to write absence (NULL, no field, an absent-run entry, no key, an empty unquoted cell)
    // differently from an empty text, so no helper in this region may turn one into the other, and no sink may replace a null by
    // "" before writing. Sync: EXPORTER_GUIDE.md § Rebuilding the display line (presence table).

    /// <summary>
    /// The text of <paramref name="text"/>, or <see langword="null"/> when absent. Evaluates a deferred text once. A set but
    /// empty text gives the empty string, never <see langword="null"/>.
    /// </summary>
    /// <param name="text">
    /// A custom text or representation; may be the default (absent) value. Passed by reference so that a text evaluated here
    /// also replaces the deferred object in the location the caller passed (a field's own storage for <c>Field.CustomText</c>).
    /// </param>
    /// <returns>The text; <see langword="null"/> exactly when the text is absent.</returns>
    internal static string? ToNullableString(in LazyString text)
    {
        if (text.IsNull)
        {
            return null;
        }

        return text.AsString;
    }

    /// <summary>
    /// Returns the custom representation string when <paramref name="include"/> is set and a representation is present;
    /// otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="value">The value whose custom representation is read.</param>
    /// <param name="include">Whether the content writes custom representations; when false nothing is read.</param>
    /// <returns>
    /// The representation; <see langword="null"/> when not included or absent, the empty string when set but empty.
    /// </returns>
    internal static string? GetCustomRepresentation(FieldValue value, bool include)
    {
        if (!include)
        {
            return null;
        }

        LazyString custom = value.CustomRepresentation;
        return custom.IsNull
            ? null
            : custom.AsString;
    }

    /// <summary>
    /// Returns the field custom text when <paramref name="include"/> is set and text is present; otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="field">The field whose custom text is read.</param>
    /// <param name="include">Whether the content writes custom texts; when false nothing is read (reading can complete a deferred text).</param>
    /// <returns>The text; <see langword="null"/> when not included or absent, the empty string when set but empty.</returns>
    internal static string? GetCustomText(Field field, bool include)
    {
        if (!include)
        {
            return null;
        }

        ref readonly LazyString text = ref field.CustomText;
        return text.IsNull
            ? null
            : text.AsString;
    }

    #endregion
}
