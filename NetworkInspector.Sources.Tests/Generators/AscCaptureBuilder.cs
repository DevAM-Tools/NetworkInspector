// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Generators;

/// <summary>
/// Builds synthetic Vector ASC text for tests: a header (absolute or relative timestamps, <c>base hex</c>), frame and
/// event lines, and the <c>End TriggerBlock</c> footer, joined with LF or CRLF.
/// </summary>
/// <remarks>This type is not thread-safe.</remarks>
internal sealed class AscCaptureBuilder
{
    #region Fields

    private const string _Date = "Wed Nov 15 00:00:00.000 2023";
    private readonly List<string> _Lines = [];
    private bool _Relative;
    private string _NewLine = "\n";

    #endregion

    #region Public API

    /// <summary>Selects <c>timestamps relative</c> (true) or <c>timestamps absolute</c> (false). Default: absolute.</summary>
    internal AscCaptureBuilder Header(bool relative)
    {
        _Relative = relative;
        return this;
    }

    /// <summary>Joins lines with CRLF (true) or LF (false, default).</summary>
    internal AscCaptureBuilder LineEnding(bool crlf)
    {
        _NewLine = crlf ? "\r\n" : "\n";
        return this;
    }

    /// <summary>Adds a classic CAN data frame line: <c>{time} {channel} {id} Rx d {dlc} {data}</c>.</summary>
    internal AscCaptureBuilder Can(string time, int channel, uint id, ReadOnlySpan<byte> data) =>
        Raw(Invariant($"{time} {channel} {id:X3} Rx d {data.Length} {_Hex(data, " ")}").TrimEnd());

    /// <summary>Adds a CAN FD line: <c>{time} CANFD {channel} Rx {id} 1 0 {dlc} {length} {data}</c>.</summary>
    internal AscCaptureBuilder CanFd(string time, int channel, uint id, ReadOnlySpan<byte> data) =>
        Raw(Invariant($"{time} CANFD {channel} Rx {id:X} 1 0 {_FdDlc(data.Length):X} {data.Length} {_Hex(data, " ")}").TrimEnd());

    /// <summary>
    /// Adds an Ethernet packet whose hex continues on following lines: the first line holds the first 6 bytes, each
    /// continuation line up to 32 bytes.
    /// </summary>
    internal AscCaptureBuilder Ethernet(string time, int channel, ReadOnlySpan<byte> frame)
    {
        int first = Math.Min(6, frame.Length);
        _ = Raw(Invariant($"{time} ETH {channel} Rx {frame.Length:x}:{_Hex(frame[..first], string.Empty)}"));
        for (int offset = first; offset < frame.Length; offset += 32)
        {
            _ = Raw(_Hex(frame.Slice(offset, Math.Min(32, frame.Length - offset)), string.Empty));
        }

        return this;
    }

    /// <summary>Adds a CAN statistic event (moves a relative clock, produces no frame).</summary>
    internal AscCaptureBuilder Statistic(string time, int channel) =>
        Raw(Invariant($"{time} {channel} Statistic: D 0 R 0 XD 0 XR 0 E 0 O 0 B 0.0%"));

    /// <summary>Adds a <c>//</c> comment line.</summary>
    internal AscCaptureBuilder Comment(string text) => Raw("// " + text);

    /// <summary>Adds a line as given.</summary>
    internal AscCaptureBuilder Raw(string line)
    {
        _Lines.Add(line);
        return this;
    }

    /// <summary>Returns the complete ASC text.</summary>
    internal string Build()
    {
        StringBuilder text = new();
        text.Append("date ").Append(_Date).Append(_NewLine);
        text.Append(_Relative ? "base hex  timestamps relative" : "base hex  timestamps absolute").Append(_NewLine);
        text.Append("no internal events logged").Append(_NewLine);
        text.Append("Begin Triggerblock ").Append(_Date).Append(_NewLine);
        foreach (string line in _Lines)
        {
            text.Append(line).Append(_NewLine);
        }

        text.Append("End TriggerBlock").Append(_NewLine);
        return text.ToString();
    }

    #endregion

    #region Helpers

    private static string _Hex(ReadOnlySpan<byte> data, string separator)
    {
        StringBuilder hex = new();
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                hex.Append(separator);
            }

            hex.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    /// <summary>CAN FD DLC code of a data length (0–8 direct, then 12, 16, 20, 24, 32, 48, 64).</summary>
    private static int _FdDlc(int length) => length switch
    {
        <= 8 => length,
        <= 12 => 9,
        <= 16 => 10,
        <= 20 => 11,
        <= 24 => 12,
        <= 32 => 13,
        <= 48 => 14,
        _ => 15,
    };

    #endregion
}
