// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.CLI.Commands;

/// <summary>
/// Parses output format specifications for frame-level conversion
/// and creates the appropriate <see cref="IFrameListener"/> exporter.
/// Supports BLF and PCAPNG output formats with per-format options.
/// </summary>
internal abstract class ConvertOutputConfig
{
    /// <summary>
    /// Creates the frame listener targeting a file path.
    /// </summary>
    internal abstract IFrameListener CreateExporter(string path, bool isStdout);

    /// <summary>
    /// Parses an output format specification string.
    /// </summary>
    /// <remarks>
    /// Supported formats:
    /// <list type="bullet">
    ///   <item><c>pcapng</c> — PCAPNG format (default)</item>
    ///   <item><c>blf</c> — BLF with default compression</item>
    ///   <item><c>blf:compression=off|none</c> — BLF, no compression</item>
    ///   <item><c>blf:compression=fast</c> — BLF, fastest compression</item>
    ///   <item><c>blf:compression=default</c> — BLF, default compression</item>
    ///   <item><c>blf:compression=best|high</c> — BLF, best compression</item>
    /// </list>
    /// </remarks>
    internal static ConvertOutputConfig Parse(string spec)
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
            "PCAPNG" or "PCAP" => new PcapngOutputConfig(),
            "BLF" => CreateBlfOutputConfig(parameters),
            _ => throw new ArgumentException($"Unknown output format: '{type}'. Supported: pcapng, blf."),
        };
    }

    /// <summary>
    /// Auto-detects the output format from a file extension.
    /// </summary>
    internal static ConvertOutputConfig FromExtension(string extension)
    {
        return extension.ToUpperInvariant() switch
        {
            ".BLF" => new BlfOutputConfig(BlfCompressionLevel.Default),
            _ => new PcapngOutputConfig(),
        };
    }

    /// <summary>Parses comma-separated key=value parameters.</summary>
    private static Dictionary<string, string> ParseParameters(string paramString)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(paramString))
        {
            return result;
        }

        foreach (string pair in paramString.Split(','))
        {
            int eqIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex > 0)
            {
                string key = pair[..eqIndex].Trim();
                string value = pair[(eqIndex + 1)..].Trim();
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Creates a <see cref="BlfOutputConfig"/> from parsed parameters.</summary>
    private static BlfOutputConfig CreateBlfOutputConfig(Dictionary<string, string> parameters)
    {
        BlfCompressionLevel level = BlfCompressionLevel.Default;

        if (parameters.TryGetValue("compression", out string? compressionStr))
        {
            level = compressionStr.ToUpperInvariant() switch
            {
                "NONE" or "OFF" or "NO" => BlfCompressionLevel.None,
                "FAST" => BlfCompressionLevel.Fast,
                "DEFAULT" or "NORMAL" => BlfCompressionLevel.Default,
                "BEST" or "HIGH" or "MAX" => BlfCompressionLevel.Best,
                _ => throw new ArgumentException(
                    $"Unknown BLF compression level: '{compressionStr}'. " +
                    "Supported: off, fast, default, best."),
            };
        }

        return new BlfOutputConfig(level);
    }
}

/// <summary>PCAPNG output format configuration.</summary>
internal sealed class PcapngOutputConfig : ConvertOutputConfig
{
    /// <inheritdoc/>
    internal override IFrameListener CreateExporter(string path, bool isStdout)
    {
        if (isStdout)
        {
            return PcapngExporter.CreateBuilder().ToStdout().Build();
        }

        return PcapngExporter.CreateBuilder().ToFile(path).Build();
    }
}

/// <summary>BLF output format configuration.</summary>
internal sealed class BlfOutputConfig(BlfCompressionLevel compression) : ConvertOutputConfig
{
    /// <summary>Compression level for the BLF output container.</summary>
    private readonly BlfCompressionLevel _Compression = compression;

    /// <inheritdoc/>
    internal override IFrameListener CreateExporter(string path, bool isStdout)
    {
        if (isStdout)
        {
            return BlfExporter.CreateBuilder()
                .ToStdout()
                .WithCompressionLevel(_Compression)
                .Build();
        }

        return BlfExporter.CreateBuilder()
            .ToFile(path)
            .WithCompressionLevel(_Compression)
            .Build();
    }
}
