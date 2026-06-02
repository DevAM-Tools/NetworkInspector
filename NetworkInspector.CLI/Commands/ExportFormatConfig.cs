// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parses export format specifications into concrete <see cref="IPacketListener"/> instances.
/// Supports format strings like <c>json:style=compact</c> or <c>pbf:format=columnar,compressed</c>.
/// </summary>
internal abstract class ExportFormatConfig
{
    /// <summary>
    /// Creates the exporter targeting a file path.
    /// </summary>
    internal abstract IPacketListener CreateFileExporter(string path, CancellationToken ct);

    /// <summary>
    /// Creates the exporter targeting stdout.
    /// </summary>
    internal abstract IPacketListener CreateStdoutExporter(CancellationToken ct);

    /// <summary>
    /// Parses a format specification string.
    /// </summary>
    /// <remarks>
    /// Supported formats:
    /// <list type="bullet">
    ///   <item><c>json</c> — default compact JSON</item>
    ///   <item><c>json:style=compact</c> — compact JSON</item>
    ///   <item><c>json:style=pretty</c> — pretty-printed JSON</item>
    ///   <item><c>json:style=array</c> — JSON array format</item>
    ///   <item><c>pbf</c> — default standard PBF</item>
    ///   <item><c>pbf:format=standard</c> — standard (row-oriented) PBF</item>
    ///   <item><c>pbf:format=columnar</c> — columnar PBF</item>
    ///   <item><c>pbf:format=columnar,compressed</c> — columnar PBF with LZ4</item>
    ///   <item><c>text</c> — human-readable protocol tree (standard detail, 256-char truncation)</item>
    ///   <item><c>text:level=summary</c> — protocol containers only</item>
    ///   <item><c>text:level=standard</c> — all fields except raw bytes (default)</item>
    ///   <item><c>text:level=full</c> — all fields including raw bytes</item>
    ///   <item><c>text:truncate=0</c> — disable value truncation</item>
    ///   <item><c>text:level=full,truncate=512</c> — full detail with 512-char truncation</item>
    /// </list>
    /// </remarks>
    internal static ExportFormatConfig Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        int colonIndex = spec.IndexOf(':', StringComparison.Ordinal);
        string type;
        string paramString;

        if (colonIndex > 0)
        {
            type = spec[..colonIndex];
            paramString = spec[(colonIndex + 1)..];
        }
        else
        {
            type = spec;
            paramString = "";
        }

        Dictionary<string, string> parameters = ParseParameters(paramString);

        return type.ToUpperInvariant() switch
        {
            "JSON" => CreateJsonConfig(parameters),
            "PBF" => CreatePbfConfig(parameters),
            "TEXT" => CreateTextConfig(parameters),
            _ => throw new ArgumentException($"Unknown export format: '{type}'. Supported: json, pbf, text."),
        };
    }

    /// <summary>
    /// Auto-detects the format from an output file extension.
    /// </summary>
    internal static ExportFormatConfig FromExtension(string extension)
    {
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonFormatConfig(JsonExportFormat.Compact);
        }

        if (extension.Equals(".pbf", StringComparison.OrdinalIgnoreCase))
        {
            return new PbfFormatConfig(PbfExportFormat.Standard, true);
        }

        if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return new TextFormatConfig(TextDetailLevel.Standard, 256);
        }

        throw new ArgumentException(
            $"Cannot auto-detect export format from extension '{extension}'. " +
            "Use -f to specify the format explicitly.");
    }

    /// <summary>
    /// Parses comma-separated key=value parameters, or bare flag tokens.
    /// </summary>
    /// <remarks>
    /// A bare token without an <c>=</c> sign (e.g., <c>compressed</c>) is treated as a
    /// boolean flag set to <c>"true"</c>; it is exactly equivalent to writing
    /// <c>compressed=true</c>.  This allows shorthands like
    /// <c>pbf:format=columnar,compressed</c>.
    /// </remarks>
    private static Dictionary<string, string> ParseParameters(string paramString)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(paramString))
        {
            return result;
        }

        foreach (string part in paramString.Split(','))
        {
            int eqIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex > 0)
            {
                string key = part[..eqIndex].Trim();
                string value = part[(eqIndex + 1)..].Trim();
                result[key] = value;
            }
            else
            {
                // Bare flag (e.g., "compressed")
                string flag = part.Trim();
                if (flag.Length > 0)
                {
                    result[flag] = "true";
                }
            }
        }

        return result;
    }

    /// <summary>Creates a JSON format config from parsed parameters.</summary>
    private static JsonFormatConfig CreateJsonConfig(Dictionary<string, string> parameters)
    {
        JsonExportFormat format = JsonExportFormat.Compact;

        if (parameters.TryGetValue("style", out string? style))
        {
            format = style.ToUpperInvariant() switch
            {
                "COMPACT" => JsonExportFormat.Compact,
                "PRETTY" => JsonExportFormat.Pretty,
                "ARRAY" => JsonExportFormat.Array,
                _ => throw new ArgumentException($"Unknown JSON style: '{style}'. Supported: compact, pretty, array."),
            };
        }

        return new JsonFormatConfig(format);
    }

    /// <summary>Creates a text format config from parsed parameters.</summary>
    private static TextFormatConfig CreateTextConfig(Dictionary<string, string> parameters)
    {
        TextDetailLevel level = TextDetailLevel.Standard;
        int maxTextLength = 256;

        if (parameters.TryGetValue("level", out string? levelStr))
        {
            level = levelStr.ToUpperInvariant() switch
            {
                "SUMMARY" => TextDetailLevel.Summary,
                "STANDARD" => TextDetailLevel.Standard,
                "FULL" => TextDetailLevel.Full,
                _ => throw new ArgumentException(
                    $"Unknown text detail level: '{levelStr}'. Supported: summary, standard, full."),
            };
        }

        if (parameters.TryGetValue("truncate", out string? truncateStr))
        {
            if (!int.TryParse(truncateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                || parsed < 0)
            {
                throw new ArgumentException(
                    $"Invalid truncate value: '{truncateStr}'. Must be a non-negative integer.");
            }

            maxTextLength = parsed;
        }

        return new TextFormatConfig(level, maxTextLength);
    }

    /// <summary>Creates a PBF format config from parsed parameters.</summary>
    private static PbfFormatConfig CreatePbfConfig(Dictionary<string, string> parameters)
    {
        PbfExportFormat format = PbfExportFormat.Standard;
        bool compressed = true;

        if (parameters.TryGetValue("format", out string? formatStr))
        {
            format = formatStr.ToUpperInvariant() switch
            {
                "STANDARD" => PbfExportFormat.Standard,
                "COLUMNAR" => PbfExportFormat.Columnar,
                _ => throw new ArgumentException(
                    $"Unknown PBF format: '{formatStr}'. Supported: standard, columnar."),
            };
        }

        if (parameters.TryGetValue("compressed", out string? compStr))
        {
            compressed = compStr.Equals("true", StringComparison.OrdinalIgnoreCase)
                || compStr == "1";
        }
        else if (parameters.ContainsKey("compressed"))
        {
            // Bare "compressed" flag
            compressed = true;
        }

        if (parameters.TryGetValue("nocompress", out _))
        {
            compressed = false;
        }

        return new PbfFormatConfig(format, compressed);
    }
}

/// <summary>JSON export format configuration.</summary>
internal sealed class JsonFormatConfig : ExportFormatConfig
{
    /// <summary>JSON output style.</summary>
    private readonly JsonExportFormat _Format;

    /// <summary>Creates a JSON format config.</summary>
    internal JsonFormatConfig(JsonExportFormat format)
    {
        _Format = format;
    }

    /// <inheritdoc/>
    internal override IPacketListener CreateFileExporter(string path, CancellationToken ct) =>
        JsonExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(_Format)
            .WithCancellationToken(ct)
            .Build();

    /// <inheritdoc/>
    internal override IPacketListener CreateStdoutExporter(CancellationToken ct) =>
        JsonExporter.CreateBuilder()
            .ToStdout()
            .WithFormat(_Format)
            .WithCancellationToken(ct)
            .Build();
}

/// <summary>PBF export format configuration.</summary>
internal sealed class PbfFormatConfig : ExportFormatConfig
{
    /// <summary>PBF block format.</summary>
    private readonly PbfExportFormat _Format;

    /// <summary>Whether to enable LZ4 compression.</summary>
    private readonly bool _Compressed;

    /// <summary>Creates a PBF format config.</summary>
    internal PbfFormatConfig(PbfExportFormat format, bool compressed)
    {
        _Format = format;
        _Compressed = compressed;
    }

    /// <inheritdoc/>
    internal override IPacketListener CreateFileExporter(string path, CancellationToken ct) =>
        PbfExporter.CreateBuilder()
            .ToFile(path)
            .WithFormat(_Format)
            .WithCompressed(_Compressed)
            .WithCancellationToken(ct)
            .Build();

    /// <inheritdoc/>
    internal override IPacketListener CreateStdoutExporter(CancellationToken ct) =>
        PbfExporter.CreateBuilder()
            .ToStdout()
            .WithFormat(_Format)
            .WithCompressed(_Compressed)
            .WithCancellationToken(ct)
            .Build();
}

/// <summary>Human-readable text export format configuration.</summary>
internal sealed class TextFormatConfig : ExportFormatConfig
{
    /// <summary>Field tree detail level.</summary>
    private readonly TextDetailLevel _Level;

    /// <summary>Maximum characters for string/bytes values. 0 = unlimited.</summary>
    private readonly int _MaxTextLength;

    /// <summary>Creates a text format config.</summary>
    internal TextFormatConfig(TextDetailLevel level, int maxTextLength)
    {
        _Level = level;
        _MaxTextLength = maxTextLength;
    }

    /// <inheritdoc/>
    internal override IPacketListener CreateFileExporter(string path, CancellationToken ct) =>
        TextExporter.CreateBuilder()
            .ToFile(path)
            .WithDetailLevel(_Level)
            .WithMaxTextLength(_MaxTextLength)
            .WithCancellationToken(ct)
            .Build();

    /// <inheritdoc/>
    internal override IPacketListener CreateStdoutExporter(CancellationToken ct) =>
        TextExporter.CreateBuilder()
            .ToStdout()
            .WithDetailLevel(_Level)
            .WithMaxTextLength(_MaxTextLength)
            .WithCancellationToken(ct)
            .Build();
}
