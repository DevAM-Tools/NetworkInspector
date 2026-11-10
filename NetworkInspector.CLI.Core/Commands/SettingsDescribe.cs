// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// <c>ni settings describe &lt;key&gt;</c> and <c>ni settings describe --limits</c>: what a setting is and, for a limit, what it
/// bounds, why it exists, what a smaller and a larger value do, and what <c>off</c> means. The text comes from the
/// <see cref="LimitDefinition"/>, the one source of every user-facing word about a limit. Output goes to the output writer in
/// the text, JSON, or (for <c>--limits</c>) Markdown form. Stateless; thread-safe.
/// </summary>
internal static class SettingsDescribe
{
    #region Types

    /// <summary>The output form of <c>describe</c>.</summary>
    internal enum DescribeFormat
    {
        /// <summary>Human-readable text.</summary>
        Text,

        /// <summary>JSON written with <see cref="Utf8JsonWriter"/>.</summary>
        Json,

        /// <summary>Markdown, only for the list of limits.</summary>
        Markdown,
    }

    /// <summary>One limit of the listing with the scope of its manager.</summary>
    /// <param name="Scope">The scope of the manager that holds the setting.</param>
    /// <param name="Setting">The limit setting; its definition is never <see langword="null"/>.</param>
    private sealed record LimitRow(SettingScope Scope, Setting Setting)
    {
        /// <summary>The definition of the limit.</summary>
        internal LimitDefinition Definition => Setting.LimitDefinition!; // a LimitRow is made only for limit settings
    }

    #endregion

    #region Parsing the format

    /// <summary>Parses the text of <c>--format</c>, ignoring case.</summary>
    /// <param name="text">The value of <c>--format</c>.</param>
    /// <returns>The format.</returns>
    /// <exception cref="ArgumentException">The text is none of <c>text</c>, <c>json</c>, <c>markdown</c>.</exception>
    internal static DescribeFormat ParseFormat(string text)
    {
        if (string.Equals(text, "text", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeFormat.Text;
        }

        if (string.Equals(text, "json", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeFormat.Json;
        }

        if (string.Equals(text, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeFormat.Markdown;
        }

        throw new ArgumentException(Invariant($"Unknown format '{text}'; use text, json, or markdown."));
    }

    #endregion

    #region One key

    /// <summary>Describes one setting.</summary>
    /// <param name="session">The plugin session that owns both managers.</param>
    /// <param name="key">The setting name.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> searches both.</param>
    /// <param name="group">The group the setting must be in; <see langword="null"/> accepts any.</param>
    /// <param name="format">Text or JSON.</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code: 0, or 1 for an unknown name.</returns>
    internal static int RunOne(CliPluginSession session, string key, SettingScope? scope, string? group, DescribeFormat format, CliIo io)
    {
        if (!SettingsLookup.TryFind(session, key, scope, group, out SettingsLookup.Match? match, out string? error))
        {
            io.Error.WriteLine("Error: " + error);
            return (int)ExitCode.ArgumentError;
        }

        // A stored value of a plugin that is not loaded has no definition, no type, and no text to describe.
        if (match.Setting is null)
        {
            io.Error.WriteLine(Invariant($"Error: '{key}' is a stored value of a plugin that is not loaded; there is nothing to describe."));
            return (int)ExitCode.ArgumentError;
        }

        if (format == DescribeFormat.Markdown)
        {
            io.Error.WriteLine("Error: --format markdown is available only with --limits.");
            return (int)ExitCode.ArgumentError;
        }

        if (format == DescribeFormat.Json)
        {
            io.Out.WriteLine(_ToJson([(match.Scope, match.Setting)], asArray: false));
            return (int)ExitCode.Success;
        }

        io.Out.Write(_ToText(match.Scope, match.Setting));
        return (int)ExitCode.Success;
    }

    #endregion

    #region All limits

    /// <summary>Lists every limit setting of the searched scopes, sorted by key.</summary>
    /// <param name="session">The plugin session that owns both managers.</param>
    /// <param name="scope">The scope to search; <see langword="null"/> searches both.</param>
    /// <param name="group">The group to list; <see langword="null"/> lists every group.</param>
    /// <param name="format">Text (a table), JSON (an array), or Markdown (a table and one section per limit).</param>
    /// <param name="io">Output and error writers.</param>
    /// <returns>The exit code: 0, or 1 for an unknown group.</returns>
    internal static int RunLimits(CliPluginSession session, SettingScope? scope, string? group, DescribeFormat format, CliIo io)
    {
        if (group is not null && !SettingsLookup.Groups(session, scope).Contains(group, StringComparer.Ordinal))
        {
            io.Error.WriteLine(Invariant(
                $"Error: Unknown group '{group}'; groups: {string.Join(", ", SettingsLookup.Groups(session, scope).Order(StringComparer.Ordinal))}."));
            return (int)ExitCode.ArgumentError;
        }

        List<LimitRow> rows = [];
        foreach ((SettingScope rowScope, SettingsManager manager) in SettingsLookup.Scopes(session, scope))
        {
            foreach (Setting setting in manager.AllSettings)
            {
                if (setting.LimitDefinition is not null
                    && (group is null || string.Equals(setting.GroupName, group, StringComparison.Ordinal)))
                {
                    rows.Add(new LimitRow(rowScope, setting));
                }
            }
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.Setting.Name, right.Setting.Name));
        switch (format)
        {
            case DescribeFormat.Json:
                io.Out.WriteLine(_ToJson([.. rows.Select(static row => (row.Scope, row.Setting))], asArray: true));
                break;
            case DescribeFormat.Markdown:
                io.Out.Write(_ToMarkdown(rows));
                break;
            default:
                io.Out.Write(_ToTable(rows));
                break;
        }

        return (int)ExitCode.Success;
    }

    #endregion

    #region Text form

    /// <summary>The text form of one setting: the lines of the header, then the paragraphs of a limit.</summary>
    /// <param name="scope">The scope of the setting.</param>
    /// <param name="setting">The setting.</param>
    /// <returns>The text, every line ended by a line break.</returns>
    private static string _ToText(SettingScope scope, Setting setting)
    {
        StringBuilder text = new();
        LimitDefinition? definition = setting.LimitDefinition;
        text.Append(CliLogFile.SingleLine(definition?.Texts.Title ?? setting.UiName)).AppendLine();
        _Line(text, "Key", setting.Name);
        if (definition is null)
        {
            _Line(text, "Type", setting.Type.ToString());
            _Line(text, "Value", SettingValueText.Format(setting, setting.Value));
            _Line(text, "Default", SettingValueText.Format(setting, setting.DefaultValue));
            _Line(text, "Scope", SettingsCommand.ScopeName(scope));
            if (!string.IsNullOrEmpty(setting.Description))
            {
                text.AppendLine().AppendLine("Description");
                _Paragraph(text, setting.Description);
            }

            return text.ToString();
        }

        _Line(text, "Unit", _UnitName(definition));
        _Line(text, "Value", definition.Format(_Current(setting)) + (setting.Value == setting.DefaultValue
            ? " (default)"
            : string.Empty));
        _Line(text, "Default", definition.Format(_DefaultOf(setting)) + " (" + _DefaultOrigin(setting, definition) + ")");
        _Line(text, "Range", definition.FormatRange());
        _Line(text, "Scope", SettingsCommand.ScopeName(scope));
        _Section(text, "What it limits", definition.Texts.Description);
        _Section(text, "Why it exists", definition.Texts.Reason);
        _Section(text, "How it helps you", definition.Texts.Benefit);
        _Section(text, "Smaller values", definition.Texts.SmallerValues);
        _Section(text, "Larger values", definition.Texts.LargerValues);
        _Section(text, "Off", definition.Texts.OffEffect);
        return text.ToString();
    }

    /// <summary>The table form of the listing: key, title, value, default, unit, range, one row per limit.</summary>
    /// <param name="rows">The limits, sorted by key.</param>
    /// <returns>The text, every line ended by a line break.</returns>
    private static string _ToTable(List<LimitRow> rows)
    {
        string[] headers = ["Key", "Title", "Value", "Default", "Unit", "Range"];
        List<string[]> cells = [headers];
        foreach (LimitRow row in rows)
        {
            LimitDefinition definition = row.Definition;
            cells.Add(
            [
                row.Setting.Name,
                CliLogFile.SingleLine(definition.Texts.Title),
                definition.Format(_Current(row.Setting)),
                definition.Format(_DefaultOf(row.Setting)),
                _UnitName(definition),
                definition.FormatRange(),
            ]);
        }

        // Every column is as wide as its widest cell; the last column is not padded, so no line ends in spaces.
        int[] widths = new int[headers.Length];
        foreach (string[] line in cells)
        {
            for (int column = 0; column < line.Length; column++)
            {
                widths[column] = Math.Max(widths[column], line[column].Length);
            }
        }

        StringBuilder table = new();
        foreach (string[] line in cells)
        {
            for (int column = 0; column < line.Length; column++)
            {
                table.Append(column == line.Length - 1
                    ? line[column]
                    : line[column].PadRight(widths[column] + 2));
            }

            table.AppendLine();
        }

        return table.ToString();
    }

    /// <summary>Appends one header line <c>  Name:  value</c>, the names aligned.</summary>
    /// <param name="text">The text being built.</param>
    /// <param name="name">The field name.</param>
    /// <param name="value">The field value.</param>
    private static void _Line(StringBuilder text, string name, string value) =>
        text.Append("  ").Append((name + ":").PadRight(10)).Append(CliLogFile.SingleLine(value)).AppendLine();

    /// <summary>Appends a blank line, the heading, and the paragraph.</summary>
    /// <param name="text">The text being built.</param>
    /// <param name="heading">The heading of the section.</param>
    /// <param name="paragraph">The paragraph.</param>
    private static void _Section(StringBuilder text, string heading, string paragraph)
    {
        text.AppendLine().AppendLine(heading);
        _Paragraph(text, paragraph);
    }

    /// <summary>Appends a paragraph indented by two spaces.</summary>
    /// <param name="text">The text being built.</param>
    /// <param name="paragraph">The paragraph.</param>
    private static void _Paragraph(StringBuilder text, string paragraph) =>
        text.Append("  ").Append(CliLogFile.SingleLine(paragraph)).AppendLine();

    #endregion

    #region JSON form

    /// <summary>
    /// The JSON form: one object for one key, an array of objects for a listing (an array also when the listing has one entry).
    /// Every limit object has the same fields; the numbers are written by <see cref="Utf8JsonWriter"/> directly.
    /// </summary>
    /// <param name="items">The settings with their scopes.</param>
    /// <param name="asArray">Whether to write an array (the listing) or the single object of one key.</param>
    /// <returns>The JSON text.</returns>
    private static string _ToJson(IReadOnlyList<(SettingScope Scope, Setting Setting)> items, bool asArray)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            if (asArray)
            {
                writer.WriteStartArray();
            }

            foreach ((SettingScope scope, Setting setting) in items)
            {
                _WriteObject(writer, scope, setting);
            }

            if (asArray)
            {
                writer.WriteEndArray();
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes the object of one setting.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="scope">The scope of the setting.</param>
    /// <param name="setting">The setting.</param>
    private static void _WriteObject(Utf8JsonWriter writer, SettingScope scope, Setting setting)
    {
        writer.WriteStartObject();
        writer.WriteString("key", setting.Name);
        writer.WriteString("group", setting.GroupName);
        writer.WriteString("scope", SettingsCommand.ScopeName(scope));
        if (setting.LimitDefinition is not { } definition)
        {
            writer.WriteString("title", setting.UiName);
            writer.WriteString("type", setting.Type.ToString());
            writer.WriteString("valueText", SettingValueText.Format(setting, setting.Value));
            writer.WriteString("defaultText", SettingValueText.Format(setting, setting.DefaultValue));
            writer.WriteString("description", setting.Description);
            writer.WriteEndObject();
            return;
        }

        LimitValue value = _Current(setting);
        writer.WriteString("title", definition.Texts.Title);
        writer.WriteString("unit", _UnitName(definition));
        _WriteNumber(writer, "value", value);
        writer.WriteString("valueText", definition.Format(value));
        _WriteNumber(writer, "default", _DefaultOf(setting));
        writer.WriteString("defaultText", definition.Format(_DefaultOf(setting)));
        writer.WriteString("defaultOrigin", _DefaultOrigin(setting, definition));
        writer.WriteNumber("minimum", definition.Minimum);
        writer.WriteNumber("maximum", definition.Maximum);
        writer.WriteString("rangeText", definition.FormatRange());
        writer.WriteBoolean("allowOff", definition.AllowOff);
        writer.WriteBoolean("readOnly", definition.ReadOnly);
        writer.WriteString("description", definition.Texts.Description);
        writer.WriteString("reason", definition.Texts.Reason);
        writer.WriteString("benefit", definition.Texts.Benefit);
        writer.WriteString("smallerValues", definition.Texts.SmallerValues);
        writer.WriteString("largerValues", definition.Texts.LargerValues);
        writer.WriteString("offEffect", definition.Texts.OffEffect);
        writer.WriteEndObject();
    }

    /// <summary>Writes a limit value as its base number, or the string <c>off</c>.</summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value.</param>
    private static void _WriteNumber(Utf8JsonWriter writer, string name, LimitValue value)
    {
        if (value.IsOff)
        {
            writer.WriteString(name, "off");
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    #endregion

    #region Markdown form

    /// <summary>The Markdown form of the listing: one table, then one section per limit with its paragraphs.</summary>
    /// <param name="rows">The limits, sorted by key.</param>
    /// <returns>The Markdown, every line ended by a line break.</returns>
    private static string _ToMarkdown(List<LimitRow> rows)
    {
        StringBuilder markdown = new();
        markdown.AppendLine("| Key | Title | Default | Unit | Range |");
        markdown.AppendLine("|-----|-------|---------|------|-------|");
        foreach (LimitRow row in rows)
        {
            LimitDefinition definition = row.Definition;
            markdown.Append("| `").Append(row.Setting.Name).Append("` | ").Append(_Cell(definition.Texts.Title)).Append(" | ")
                .Append(_Cell(definition.Format(_DefaultOf(row.Setting)))).Append(" | ").Append(_Cell(_UnitName(definition))).Append(" | ")
                .Append(_Cell(definition.FormatRange())).AppendLine(" |");
        }

        foreach (LimitRow row in rows)
        {
            LimitDefinition definition = row.Definition;
            markdown.AppendLine().Append("## `").Append(row.Setting.Name).AppendLine("`").AppendLine();
            markdown.Append("**").Append(CliLogFile.SingleLine(definition.Texts.Title)).AppendLine("**").AppendLine();
            _MarkdownParagraph(markdown, "What it limits", definition.Texts.Description);
            _MarkdownParagraph(markdown, "Why it exists", definition.Texts.Reason);
            _MarkdownParagraph(markdown, "How it helps you", definition.Texts.Benefit);
            _MarkdownParagraph(markdown, "Smaller values", definition.Texts.SmallerValues);
            _MarkdownParagraph(markdown, "Larger values", definition.Texts.LargerValues);
            _MarkdownParagraph(markdown, "Off", definition.Texts.OffEffect);
        }

        return markdown.ToString();
    }

    /// <summary>Appends <c>**Heading.** paragraph</c> and a blank line.</summary>
    /// <param name="markdown">The Markdown being built.</param>
    /// <param name="heading">The label of the paragraph.</param>
    /// <param name="paragraph">The paragraph.</param>
    private static void _MarkdownParagraph(StringBuilder markdown, string heading, string paragraph) =>
        markdown.Append("**").Append(heading).Append(".** ").Append(CliLogFile.SingleLine(paragraph)).AppendLine().AppendLine();

    /// <summary>Escapes a table cell: one line, and the pipe character does not end the cell.</summary>
    /// <param name="text">The cell text.</param>
    /// <returns>The escaped text.</returns>
    private static string _Cell(string text) => CliLogFile.SingleLine(text).Replace("|", "\\|", StringComparison.Ordinal);

    #endregion

    #region Shared pieces

    /// <summary>The current value of a limit setting.</summary>
    /// <param name="setting">A limit setting.</param>
    /// <returns>The current value.</returns>
    private static LimitValue _Current(Setting setting) =>
        setting.TryGetAsLimit(out LimitValue value)
            ? value
            : default;

    /// <summary>The default of a limit setting: the fixed number of the definition, or the one the application chose.</summary>
    /// <param name="setting">A limit setting.</param>
    /// <returns>The default value.</returns>
    private static LimitValue _DefaultOf(Setting setting) =>
        setting.DefaultValue.TryGetAsLimit(out LimitValue value)
            ? value
            : default;

    /// <summary>The name of the unit of a limit, for the header of the text and the JSON field <c>unit</c>.</summary>
    /// <param name="definition">The limit.</param>
    /// <returns>The name of a table unit (<c>bytes</c>, <c>durations</c>, <c>counts</c>), otherwise an example value.</returns>
    private static string _UnitName(LimitDefinition definition) =>
        definition.Unit is TableUnit table
            ? table.Name
            : "e.g. " + definition.Unit.Example;

    /// <summary>
    /// Where the default comes from: <c>fixed</c> when it is the number of the definition, <c>application</c> when the application
    /// that created the manager chose another default.
    /// </summary>
    /// <param name="setting">The limit setting.</param>
    /// <param name="definition">Its definition.</param>
    /// <returns>The origin word.</returns>
    private static string _DefaultOrigin(Setting setting, LimitDefinition definition) =>
        setting.DefaultValue == SettingValue.Limit(definition.DefaultValue)
            ? "fixed"
            : "chosen by the application";

    #endregion
}
