// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Shared tshark verification facade used by all Network Inspector test projects
/// (<c>NetworkInspector.Protocols.Tests</c>, <c>NetworkInspector.Exporters.Tests</c>,
/// future source/exporter test projects). Combines:
/// <list type="bullet">
///   <item>availability/escape-hatch handling (<see cref="IsAvailable"/>,
///         <see cref="RequireAvailable"/>, <see cref="MissingTsharkAllowed"/>);</item>
///   <item>in-memory frame extraction by writing a temporary single-frame PCAP
///         (<see cref="GetFieldValue"/>, <see cref="GetFieldValues"/>,
///         <see cref="GetDisplayText"/>, <see cref="GetPdmlField"/>,
///         <see cref="GetPdmlFields"/>, <see cref="GetProtocolFields"/>);</item>
///   <item>capture-file extraction for roundtrip verification (<see cref="GetPacketCount"/>,
///         <see cref="GetPacketRecords"/>, <see cref="GetInterfaceNames"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Mandatory by default.</b> All extraction methods throw
/// <see cref="InvalidOperationException"/> when tshark is missing on <c>PATH</c>, so a
/// release/CI run cannot silently lose its cross-validation evidence.</para>
/// <para><b>Local developer escape hatch.</b> Setting
/// <c>NETWORKINSPECTOR_ALLOW_MISSING_TSHARK=1</c> downgrades the missing-tshark error to
/// a silent skip — frame-extraction methods then return <see langword="null"/> / empty
/// results so individual tests can opt-in to skipping with <see cref="TsharkAvailability.ShouldSkip"/>.</para>
/// <para><b>Thread safety.</b> The type is fully static; the cached availability flag is
/// written at most once. Safe for concurrent use across parallel tests.</para>
/// </remarks>
public static class TsharkVerifier
{
    /// <summary>
    /// Environment variable name. When set to <c>"1"</c> the missing-tshark hard failure
    /// is downgraded to a silent skip. Intended only for developer machines without
    /// Wireshark; CI/release runs must leave it unset.
    /// </summary>
    public const string AllowMissingEnvVar = "NETWORKINSPECTOR_ALLOW_MISSING_TSHARK";

    /// <summary>Cached availability result (<see langword="null"/> = not yet probed).</summary>
    private static bool? _IsAvailable;

    /// <summary>
    /// Returns <see langword="true"/> when the developer opted into the missing-tshark
    /// escape hatch via <see cref="AllowMissingEnvVar"/>.
    /// </summary>
    public static bool MissingTsharkAllowed
        => string.Equals(
            Environment.GetEnvironmentVariable(AllowMissingEnvVar),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Probes <c>tshark --version</c> and caches the result. Returns <see langword="true"/>
    /// when tshark is available on <c>PATH</c>.
    /// </summary>
    public static bool IsAvailable()
    {
        if (_IsAvailable.HasValue)
        {
            return _IsAvailable.Value;
        }

        try
        {
            (int exit, _, _, _) = TsharkProcess.Run("--version", 5000);
            _IsAvailable = exit == 0;
        }
        catch
        {
            _IsAvailable = false;
        }

        return _IsAvailable.Value;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when tshark is not installed.
    /// Used by tests that require an external reference implementation; missing tshark
    /// must fail loudly rather than silently passing.
    /// </summary>
    public static void RequireAvailable()
    {
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "tshark is required for cross-validation tests but was not found on PATH. " +
                "Install Wireshark/tshark 4.6.x or newer (tshark must be on PATH).");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when tshark is present, <see langword="false"/>
    /// when tshark is missing and the escape hatch is enabled. Throws otherwise so a
    /// release/CI run cannot lose its evidence silently.
    /// </summary>
    private static bool _EnsureAvailableOrAllowed()
    {
        if (IsAvailable())
        {
            return true;
        }

        if (MissingTsharkAllowed)
        {
            return false;
        }

        throw new InvalidOperationException(
            "tshark is required for protocol cross-validation but was not found on PATH. " +
            $"Install Wireshark/tshark or set {AllowMissingEnvVar}=1 to skip cross-validation locally. " +
            "Release and CI runs must execute with tshark available.");
    }

    #region In-memory frame extraction (writes a temp PCAP)

    /// <summary>
    /// Extracts a single field value from the first packet using <c>tshark -T fields</c>.
    /// Returns <see langword="null"/> when the field is absent or when the escape hatch
    /// is active and tshark is missing.
    /// </summary>
    /// <param name="frameData">Raw frame bytes for the link-layer type indicated by <paramref name="dlt"/>.</param>
    /// <param name="tsharkFieldName">tshark field name (for example <c>ip.src</c>, <c>tcp.dstport</c>).</param>
    /// <param name="dlt">Wireshark/libpcap link-type identifier (DLT). Default <c>1</c> = Ethernet.</param>
    /// <param name="decodeAs">Optional <c>-d</c> argument forcing dissector selection (for example <c>tcp.port==8443,http2</c>).</param>
    /// <param name="profileDir">Optional per-test tshark profile directory; passed through to <see cref="TsharkProcess.Run"/>.</param>
    public static string? GetFieldValue(
        byte[] frameData,
        string tsharkFieldName,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        string?[] values = GetFieldValues(frameData, [tsharkFieldName], dlt, decodeAs, profileDir);
        if (values.Length == 0)
        {
            return null;
        }

        return values[0];
    }

    /// <summary>
    /// Extracts multiple field values from the first packet in a single tshark
    /// invocation. Returns an array parallel to <paramref name="tsharkFieldNames"/>;
    /// entries are <see langword="null"/> when the corresponding field is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>tshark -T fields -e …</c> validates each <c>-e</c> name against the Wireshark startup field
    /// registry only. Preference/UAT-registered Signal Message per-signal columns are thus rejected as
    /// &quot;Some fields aren't valid&quot; even though the dissected PDML tree contains those fields at
    /// runtime. When <paramref name="profileDir"/> is set and stderr reports that error for the requested
    /// names, values are fetched from one <c>-T pdml</c> parse instead.
    /// </para>
    /// </remarks>
    public static string?[] GetFieldValues(
        byte[] frameData,
        string[] tsharkFieldNames,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        foreach (string name in tsharkFieldNames)
        {
            _ValidateTsharkFieldName(name);
        }

        if (!_EnsureAvailableOrAllowed())
        {
            return new string?[tsharkFieldNames.Length];
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"ni_test_{Guid.NewGuid():N}.pcap");
        try
        {
            TsharkPcapWriter.Write(tempFile, frameData, dlt);

            string fieldArgs = string.Join(" ", tsharkFieldNames.Select(f => $"-e {f}"));
            string decodeArg = string.IsNullOrEmpty(decodeAs) ? string.Empty : $" -d {decodeAs}";
            string arguments = $"-r \"{tempFile}\"{decodeArg} -T fields {fieldArgs} -c 1";

            (int exit, string output, string stderrOut, _) = TsharkProcess.Run(arguments, 10_000, profileDir);
            if (exit != 0)
            {
                bool rejectedDynamicFields =
                    !string.IsNullOrEmpty(profileDir)
                    && stderrOut.Contains("Some fields aren't valid", StringComparison.OrdinalIgnoreCase);
                if (rejectedDynamicFields)
                {
                    return _ExtractFieldStringsFromPdml(frameData, tsharkFieldNames, dlt, decodeAs, profileDir);
                }

                return new string?[tsharkFieldNames.Length];
            }

            // Take the last non-empty line: user Lua plugins (when present) sometimes
            // print extra lines first. Lua plugins are normally suppressed via
            // WIRESHARK_CONFIG_DIR but this is a defensive fallback.
            string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string line = lines.Length > 0 ? lines[^1] : string.Empty;
            string[] parts = line.Split('\t');
            string?[] result = new string?[tsharkFieldNames.Length];
            for (int i = 0; i < tsharkFieldNames.Length; i++)
            {
                result[i] = i < parts.Length && !string.IsNullOrEmpty(parts[i]) ? parts[i] : null;
            }
            return result;
        }
        finally
        {
            _TryDelete(tempFile);
        }
    }

    private static string?[] _ExtractFieldStringsFromPdml(
        byte[] frameData,
        string[] tsharkFieldNames,
        int dlt,
        string? decodeAs,
        string? profileDir)
    {
        List<PdmlField> matched = GetPdmlFields(frameData, tsharkFieldNames, dlt, decodeAs, profileDir);
        // StringComparer.Ordinal is correct — field names are validated lowercase-only by
        // _ValidateTsharkFieldName, and PDML output from tshark uses lowercase names.
        Dictionary<string, PdmlField> byName = new(StringComparer.Ordinal);
        foreach (PdmlField f in matched)
        {
            /*
             Repeated names are rare within the first dissected PDU; overwrite keeps the latest match.
            */
            byName[f.Name] = f;
        }

        string?[] result = new string?[tsharkFieldNames.Length];
        for (int i = 0; i < tsharkFieldNames.Length; i++)
        {
            string name = tsharkFieldNames[i];
            if (byName.TryGetValue(name, out PdmlField pf))
            {
                result[i] = _PdmlComparableString(pf);
            }
        }

        return result;
    }

    private static string? _PdmlComparableString(PdmlField field)
    {
        if (!string.IsNullOrWhiteSpace(field.Show))
        {
            return field.Show.Trim();
        }

        if (string.IsNullOrWhiteSpace(field.Value))
        {
            return null;
        }

        return field.Value.Trim();
    }

    /// <summary>
    /// Returns the PDML <c>show</c> attribute (display text) of a single field from the
    /// first packet, or <see langword="null"/> when the field is absent.
    /// </summary>
    public static string? GetDisplayText(
        byte[] frameData,
        string tsharkFieldName,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        PdmlField? field = GetPdmlField(frameData, tsharkFieldName, dlt, decodeAs, profileDir);
        return field?.Show;
    }

    /// <summary>
    /// Extracts the full PDML information for a single field from the first packet, or
    /// <see langword="null"/> when the field is absent.
    /// </summary>
    public static PdmlField? GetPdmlField(
        byte[] frameData,
        string tsharkFieldName,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        List<PdmlField> fields = GetPdmlFields(frameData, [tsharkFieldName], dlt, decodeAs, profileDir);
        if (fields.Count == 0)
        {
            return null;
        }

        return fields[0];
    }

    /// <summary>
    /// Extracts the PDML information for multiple fields from the first packet. The
    /// result list may contain fewer entries than requested if fields are absent.
    /// </summary>
    public static List<PdmlField> GetPdmlFields(
        byte[] frameData,
        string[] tsharkFieldNames,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        if (!_EnsureAvailableOrAllowed())
        {
            return [];
        }

        string output = _RunPdml(frameData, dlt, decodeAs, profileDir);
        return string.IsNullOrWhiteSpace(output)
            ? []
            : _ParsePdmlFields(output, tsharkFieldNames);
    }

    /// <summary>
    /// Extracts every PDML field belonging to the given tshark protocol from the first
    /// packet. Useful for comprehensive validation of all fields in a protocol layer.
    /// </summary>
    /// <param name="frameData">Raw frame bytes.</param>
    /// <param name="protocolName">tshark protocol name (for example <c>eth</c>, <c>ip</c>, <c>tcp</c>).</param>
    /// <param name="dlt">Wireshark/libpcap link-type identifier (DLT).</param>
    /// <param name="decodeAs">Optional <c>-d</c> dissector override.</param>
    /// <param name="profileDir">Optional per-test tshark profile directory.</param>
    public static List<PdmlField> GetProtocolFields(
        byte[] frameData,
        string protocolName,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        if (!_EnsureAvailableOrAllowed())
        {
            return [];
        }

        string output = _RunPdml(frameData, dlt, decodeAs, profileDir);
        return string.IsNullOrWhiteSpace(output)
            ? []
            : _ParsePdmlProtocolFields(output, protocolName);
    }

    /// <summary>
    /// Helper: writes <paramref name="frameData"/> to a temp PCAP, invokes tshark with
    /// <c>-T pdml -c 1</c>, and returns the raw PDML XML. Returns an empty string when
    /// tshark fails.
    /// </summary>
    private static string _RunPdml(byte[] frameData, int dlt, string? decodeAs, string? profileDir = null)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"ni_test_{Guid.NewGuid():N}.pcap");
        try
        {
            TsharkPcapWriter.Write(tempFile, frameData, dlt);
            string decodeArg = string.IsNullOrEmpty(decodeAs) ? string.Empty : $" -d {decodeAs}";
            string arguments = $"-r \"{tempFile}\"{decodeArg} -T pdml -c 1";
            (int exit, string output, string stderrOut, _) = TsharkProcess.Run(arguments, 10_000, profileDir);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"tshark -T pdml failed with exit code {exit}. stderr:\n{stderrOut}");
            }

            return output;
        }
        finally
        {
            _TryDelete(tempFile);
        }
    }

    /// <summary>
    /// Validates that a tshark field name contains only lowercase ASCII letters, ASCII digits,
    /// dots, and underscores. This whitelist prevents command-line injection (CWE-78) by
    /// ensuring a caller-supplied name cannot inject additional tshark options via -e arguments.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fieldName"/> is empty or
    /// contains characters outside the allowed set.</exception>
    private static void _ValidateTsharkFieldName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw new ArgumentException("tshark field name cannot be empty or whitespace.", nameof(fieldName));
        }

        foreach (char c in fieldName)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '.' && c != '_')
            {
                throw new ArgumentException(
                    $"Invalid tshark field name '{fieldName}': character '{c}' is not allowed. " +
                    "Only lowercase ASCII letters, digits, dots, and underscores are permitted.",
                    nameof(fieldName));
            }
        }
    }

    /// <summary>Best-effort temp file removal that swallows IO errors.</summary>
    private static void _TryDelete(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (FileNotFoundException)
            {
                // File already gone — nothing to do.
                return;
            }
            catch (IOException ex)
            {
                if (attempt < 2)
                {
                    // Windows can hold a file open briefly after async stream draining.
                    // Retry with a short delay before giving up.
                    Debug.WriteLine(
                        $"[TsharkVerifier] Temp file '{path}' still locked (attempt {attempt + 1}/3): {ex.Message}. Retrying...");
                    Thread.Sleep(50);
                }
                else
                {
                    // All three attempts exhausted — log and abandon cleanup.
                    Debug.WriteLine(
                        $"[TsharkVerifier] Failed to delete temp file '{path}' after 3 attempts: {ex.Message}");
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // Log to debug output so permissions issues are visible during development
                // without surfacing as test failures.
                Debug.WriteLine($"[TsharkVerifier] Failed to delete temp file '{path}': {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// Parses PDML XML and extracts the <see cref="PdmlField"/> entries whose
    /// <c>name</c> attribute matches one of <paramref name="fieldNames"/>. Searches
    /// recursively through all nested <c>&lt;field&gt;</c> elements.
    /// </summary>
    private static List<PdmlField> _ParsePdmlFields(string pdmlXml, string[] fieldNames)
        => PdmlParser.ParseFields(pdmlXml, fieldNames);

    private static List<PdmlField> _ParsePdmlProtocolFields(string pdmlXml, string protocolName)
        => PdmlParser.ParseProtocolFields(pdmlXml, protocolName);

    #endregion

    #region Multi-frame in-memory extraction

    /// <summary>
    /// Writes <paramref name="frames"/> to a temporary multi-frame PCAP and runs
    /// <c>tshark -T fields</c> over the entire capture, returning one row per
    /// packet. Each returned array is parallel to <paramref name="tsharkFieldNames"/>;
    /// missing fields are <see langword="null"/>.  Returns an empty list when the
    /// escape hatch is enabled and tshark is missing.
    /// </summary>
    /// <remarks>
    /// Use this when validation must consider stream/conversation context (TCP
    /// reassembly, per-conversation flag tracking, retransmission detection, …)
    /// rather than dissecting frames in isolation.
    /// </remarks>
    /// <param name="frames">Frames in wire order.</param>
    /// <param name="tsharkFieldNames">Field names to extract per packet.</param>
    /// <param name="dlt">Wireshark/libpcap link-type identifier (DLT). Default <c>1</c> = Ethernet.</param>
    /// <param name="decodeAs">Optional <c>-d</c> dissector override.</param>
    /// <param name="profileDir">Optional per-test tshark profile directory.</param>
    public static List<string?[]> GetFieldValuesPerPacket(
        IReadOnlyList<byte[]> frames,
        string[] tsharkFieldNames,
        int dlt = 1,
        string? decodeAs = null,
        string? profileDir = null)
    {
        foreach (string name in tsharkFieldNames)
        {
            _ValidateTsharkFieldName(name);
        }

        if (!_EnsureAvailableOrAllowed())
        {
            return [];
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"ni_test_{Guid.NewGuid():N}.pcap");
        try
        {
            TsharkPcapWriter.Write(tempFile, frames, dlt);

            string fieldArgs = string.Join(" ", tsharkFieldNames.Select(f => $"-e {f}"));
            string decodeArg = string.IsNullOrEmpty(decodeAs) ? string.Empty : $" -d {decodeAs}";
            // -E header=n: never emit a header row.  -E occurrence=f: when a field appears
            // multiple times in a frame (TCP option lists, repeated TLS records, …),
            // take the first occurrence so the column count stays stable.
            string arguments =
                $"-r \"{tempFile}\"{decodeArg} -T fields -E header=n -E separator=/t -E occurrence=f {fieldArgs}";

            (int exit, string output, string stderr, _) = TsharkProcess.Run(arguments, 15_000, profileDir);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"tshark failed (exit {exit}) while extracting per-packet fields: {stderr.Trim()}");
            }

            List<string?[]> rows = [];
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }
                string[] parts = line.Split('\t');
                string?[] row = new string?[tsharkFieldNames.Length];
                for (int i = 0; i < tsharkFieldNames.Length; i++)
                {
                    row[i] = i < parts.Length && parts[i].Length > 0 ? parts[i] : null;
                }
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            _TryDelete(tempFile);
        }
    }

    /// <summary>
    /// Writes <paramref name="frames"/> to a temporary multi-frame PCAP and returns
    /// the raw <c>tshark</c> output produced by an arbitrary argument list (use
    /// <c>{0}</c> as a placeholder for the <c>-r &lt;file&gt;</c> input argument).
    /// Used by tests that need <c>-z</c> statistics, <c>-Y</c> display filters, or
    /// <c>follow,tcp,raw,...</c> output that the structured extractors don't cover.
    /// </summary>
    /// <param name="frames">Frames in wire order.</param>
    /// <param name="argumentsAfterReader">tshark arguments appended after the implicit <c>-r &lt;file&gt;</c>.</param>
    /// <param name="dlt">DLT for the temporary capture.</param>
    /// <param name="profileDir">Optional per-test tshark profile directory.</param>
    public static string RunOnFrames(
        IReadOnlyList<byte[]> frames,
        string argumentsAfterReader,
        int dlt = 1,
        string? profileDir = null)
    {
        if (!_EnsureAvailableOrAllowed())
        {
            return string.Empty;
        }

        string tempFile = Path.Combine(Path.GetTempPath(), $"ni_test_{Guid.NewGuid():N}.pcap");
        try
        {
            TsharkPcapWriter.Write(tempFile, frames, dlt);
            string arguments = $"-r \"{tempFile}\" {argumentsAfterReader}";
            (int exit, string output, string stderr, _) = TsharkProcess.Run(arguments, 15_000, profileDir);
            if (exit != 0)
            {
                throw new InvalidOperationException(
                    $"tshark failed (exit {exit}) for arguments '{argumentsAfterReader}': {stderr.Trim()}");
            }
            return output;
        }
        finally
        {
            _TryDelete(tempFile);
        }
    }

    #endregion

    #region Capture-file extraction (used by exporter roundtrip tests)

    /// <summary>
    /// Returns the number of packets that tshark can read from a capture file
    /// (PCAPNG, BLF, …). Throws when tshark is unavailable or the file is invalid.
    /// </summary>
    /// <param name="filePath">Path to the capture file.</param>
    /// <param name="timeoutMs">tshark execution timeout in milliseconds.</param>
    public static int GetPacketCount(string filePath, int timeoutMs = 30_000)
    {
        (int exit, string output, string stderr, bool timedOut) = TsharkProcess.Run(
            $"-r \"{filePath}\" -T fields -e frame.number",
            timeoutMs);

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"tshark timed out after {timeoutMs} ms while counting packets in '{filePath}'.");
        }
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"tshark failed with exit code {exit}: {stderr}");
        }

        // Count only numeric lines so any stray non-packet output is ignored.
        int count = 0;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Reads every packet from <paramref name="filePath"/> via a single
    /// <c>tshark -T fields</c> invocation and returns the per-frame metadata used by
    /// roundtrip verifications. Throws on tshark failure with the offending capture
    /// preserved under the system temp directory for manual reproduction.
    /// </summary>
    public static List<TsharkRecord> GetPacketRecords(string filePath, int timeoutMs = 60_000)
    {
        (int exit, string stdout, string stderr, bool timedOut) = TsharkProcess.Run(
            $"-r \"{filePath}\" -n -T fields " +
            "-E separator=/t -E quote=n -E header=n " +
            "-e frame.number -e frame.time_epoch -e frame.len " +
            "-e frame.interface_name -e frame.interface_id -e frame.encap_type",
            timeoutMs);

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"tshark timed out after {timeoutMs} ms while reading '{filePath}'.");
        }
        if (exit != 0)
        {
            string copyPath = _PreserveCaptureForDiagnostics(filePath);
            throw new InvalidOperationException(
                $"tshark failed with exit code {exit}: {stderr.Trim()}\n" +
                $"Capture preserved at: {copyPath}");
        }

        List<TsharkRecord> records = [];
        // Pre-allocate the Range buffer once; MemoryExtensions.Split writes results in-place.
        Span<Range> ranges = stackalloc Range[8];
        foreach (string line in stdout.Split('\n'))
        {
            // Use a span-based tab split (stackalloc ranges) to eliminate per-line
            // string array + per-field string allocations from string.Split('\t').
            ReadOnlySpan<char> trimmed = line.AsSpan().TrimEnd('\r');
            if (trimmed.IsEmpty)
            {
                continue;
            }

            int fieldCount = MemoryExtensions.Split(trimmed, ranges, '\t');
            if (fieldCount < 6)
            {
                throw new InvalidOperationException(
                    $"Unexpected tshark output line (need >= 6 tab-separated fields): '{new string(trimmed)}'.\n" +
                    $"stderr: {stderr}");
            }

            if (!int.TryParse(trimmed[ranges[0]], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frameNumber))
            {
                // Non-numeric leading field → not a packet row (defensive: Lua plugins).
                continue;
            }

            long timeNanos;
            int frameLen;
            int interfaceId;
            int encapType;
            try
            {
                timeNanos = _ParseEpochNanos(trimmed[ranges[1]]);
                frameLen = int.Parse(trimmed[ranges[2]], CultureInfo.InvariantCulture);
                ReadOnlySpan<char> field4 = trimmed[ranges[4]];
                ReadOnlySpan<char> field5 = trimmed[ranges[5]];
                interfaceId = field4.IsEmpty ? 0 : int.Parse(field4, CultureInfo.InvariantCulture);
                encapType = field5.IsEmpty ? 0 : int.Parse(field5, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                string rowText = new string(trimmed).Replace("\t", "\\t", StringComparison.Ordinal);
                string copyPath = _PreserveCaptureForDiagnostics(filePath);
                throw new InvalidOperationException(
                    $"Failed to parse tshark output row #{frameNumber} from '{filePath}': {ex.Message}\n" +
                    $"Row (tab-escaped): '{rowText}'\n" +
                    $"Capture preserved at: {copyPath}\n" +
                    $"tshark stderr: {stderr.Trim()}", ex);
            }

            string interfaceName = new string(trimmed[ranges[3]]);
            records.Add(new TsharkRecord(frameNumber, timeNanos, frameLen, interfaceName, interfaceId, encapType));
        }

        return records;
    }

    /// <summary>
    /// Returns the distinct interface names reported by tshark for the capture, in the
    /// order they were first seen. Used by multi-interface roundtrip tests.
    /// </summary>
    public static List<string> GetInterfaceNames(string filePath, int timeoutMs = 60_000)
    {
        (int exit, string stdout, string stderr, bool timedOut) = TsharkProcess.Run(
            $"-r \"{filePath}\" -n -T fields -E separator=/t -E quote=n -E header=n " +
            "-e frame.interface_name -e frame.interface_id",
            timeoutMs);

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"tshark timed out after {timeoutMs} ms while reading interfaces from '{filePath}'.");
        }
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"tshark failed with exit code {exit}: {stderr}");
        }

        Dictionary<int, string> byId = [];
        List<int> orderedIds = [];
        foreach (string line in stdout.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string[] parts = trimmed.Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            int id = parts[1].Length == 0 ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (!byId.ContainsKey(id))
            {
                byId[id] = parts[0];
                orderedIds.Add(id);
            }
        }

        List<string> result = new(orderedIds.Count);
        foreach (int id in orderedIds)
        {
            result.Add(byId[id]);
        }
        return result;
    }

    /// <summary>
    /// Copies the capture file into <see cref="Path.GetTempPath"/> under a stable
    /// diagnostic name so it survives test cleanup and the developer can re-run tshark
    /// by hand when an assertion fails.
    /// </summary>
    private static string _PreserveCaptureForDiagnostics(string filePath)
    {
        string copyPath = Path.Combine(
            Path.GetTempPath(),
            "tshark_dbg_" + Path.GetFileName(filePath));
        try
        {
            File.Copy(filePath, copyPath, overwrite: true);
        }
        catch
        {
            // Best effort — preserve diagnostics but do not mask the real failure.
        }
        return copyPath;
    }

    /// <summary>
    /// Parses a tshark <c>frame.time_epoch</c> value such as
    /// <c>1745432123.123456789</c> into nanoseconds since the Unix epoch. Missing
    /// fractional digits are zero-padded; extra digits are truncated to nanosecond
    /// precision. Throws with the offending value on empty/unparseable input.
    /// </summary>
    private static long _ParseEpochNanos(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            throw new InvalidOperationException(
                "tshark returned an empty 'frame.time_epoch' value. " +
                "This usually means the capture has frames without a usable timestamp.");
        }

        int dot = value.IndexOf('.');
        long secs;
        long nanos;
        if (dot < 0)
        {
            secs = long.Parse(value, CultureInfo.InvariantCulture);
            nanos = 0;
        }
        else
        {
            secs = long.Parse(value[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture);
            ReadOnlySpan<char> frac = value[(dot + 1)..];

            // Validate that every fractional character is an ASCII digit.
            for (int i = 0; i < frac.Length; i++)
            {
                if (!char.IsAsciiDigit(frac[i]))
                {
                    throw new InvalidOperationException(
                        $"Malformed fractional seconds in '{new string(value)}': unexpected character '{frac[i]}' at position {i}.");
                }
            }

            // Parse exactly 9 digits (nanosecond precision).
            // Extra digits are truncated; shorter fractions are right-padded with zeros
            // using a stack-allocated char buffer to avoid heap allocation on this hot path.
            if (frac.Length > 9)
            {
                nanos = long.Parse(frac[..9], NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            else if (frac.Length < 9)
            {
                Span<char> padded = stackalloc char[9];
                frac.CopyTo(padded);
                padded[frac.Length..].Fill('0');
                nanos = long.Parse(padded, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            else
            {
                nanos = long.Parse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }

            if (nanos < 0 || nanos >= 1_000_000_000L)
            {
                throw new InvalidOperationException(
                    $"Parsed nanoseconds value {nanos} is out of range [0, 999999999] for '{new string(value)}'.");
            }
        }

        return (secs * 1_000_000_000L) + nanos;
    }

    #endregion
}
