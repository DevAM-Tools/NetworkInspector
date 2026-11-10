// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Writes one UTF-8 line per packet that carries the sample fields; packets without them are counted as skipped. If the
/// listener is disposed without <see cref="OnFinish"/>, it deletes the partial file (the contract rule for exporters).
/// <para>
/// Thread safety: single writer. The export runner calls <see cref="OnPacket"/>, <see cref="OnFinish"/>, and
/// <see cref="Dispose"/> from one thread; the statistics are readable from other threads.
/// </para>
/// </summary>
internal sealed class SampleTextListener : IPacketListener, IExporterStatistics, IExportByteProgress, IDisposable
{
    #region Constants

    private const string _HeaderLine = "packet_id\tcounter\tgear\tspeed";

    // Longest decimal text of an ulong is 20 digits; the scratch buffer holds one number.
    private const int _NumberBufferLength = 24;

    #endregion

    #region Fields

    private readonly string _Path;

    private readonly FieldId _CounterField;

    private readonly FieldId _GearField;

    private readonly FieldId _SpeedField;

    private StreamWriter? _Writer;

    private long _Bytes;

    private int _Written;

    private int _Skipped;

    private int _Finished;

    #endregion

    #region Lifecycle

    /// <summary>Creates a listener over an already created exclusive file stream.</summary>
    /// <param name="stream">The newly created output file; the listener owns and closes it.</param>
    /// <param name="path">Full path of the file, used to delete it when the export did not finish.</param>
    /// <param name="includeHeader">Whether to write the header line first.</param>
    /// <param name="counter">Field id of <c>sample.counter</c>.</param>
    /// <param name="gear">Field id of <c>sample.gear</c>.</param>
    /// <param name="speed">Field id of <c>sample.speed</c>.</param>
    internal SampleTextListener(FileStream stream, string path, bool includeHeader, FieldId counter, FieldId gear, FieldId speed)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(path);
        _Path = path;
        _CounterField = counter;
        _GearField = gear;
        _SpeedField = speed;
        _Writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        if (includeHeader)
        {
            _WriteLine(_HeaderLine);
        }
    }

    /// <summary>
    /// Closes the file. When <see cref="OnFinish"/> was not called, the partial output is deleted. Deleting is best effort and
    /// never throws, because disposal runs on cleanup paths: if the file stays (locked by a scanner, for example), the
    /// application's export runner removes the target afterwards and reports a failure there.
    /// </summary>
    public void Dispose()
    {
        StreamWriter? writer = Interlocked.Exchange(ref _Writer, null);
        if (writer is null)
        {
            return;
        }

        writer.Dispose();
        if (Volatile.Read(ref _Finished) == 0)
        {
            try
            {
                File.Delete(_Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Intentionally ignored, see the summary: the export runner owns the final cleanup and its report.
            }
        }
    }

    #endregion

    #region Public API

    /// <inheritdoc/>
    public string UiName => "Sample text";

    /// <inheritdoc/>
    public string? Description => "One line per sample packet: id, counter, gear, speed.";

    /// <inheritdoc/>
    public bool OnPacket(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        StreamWriter writer = _Writer ?? throw new ObjectDisposedException(nameof(SampleTextListener));

        // A packet after OnFinish would extend a file that was already reported as complete: a bug of the caller.
        if (Volatile.Read(ref _Finished) != 0)
        {
            throw new InvalidOperationException("OnPacket was called after OnFinish.");
        }

        // Packets of other protocols are not errors: they are counted and skipped.
        if (!packet.TryGetFieldValue(_CounterField, out FieldValue counter, materialize: true)
            || !packet.TryGetFieldValue(_GearField, out FieldValue gear, materialize: true)
            || !packet.TryGetFieldValue(_SpeedField, out FieldValue speed, materialize: true))
        {
            Interlocked.Increment(ref _Skipped);
            return true;
        }

        // One line: id, counter, gear, speed, formatted into a stack buffer without intermediate strings. The size counts the
        // characters handed to the writer; the text is ASCII, so characters equal UTF-8 bytes. Three tabs and the line feed
        // add four.
        Span<char> number = stackalloc char[_NumberBufferLength];
        long lineBytes = 4;
        lineBytes += _WriteNumber(writer, packet.Id.Value, number);
        writer.Write('\t');
        lineBytes += _WriteValue(writer, counter, number);
        writer.Write('\t');
        lineBytes += _WriteValue(writer, gear, number);
        writer.Write('\t');
        lineBytes += _WriteValue(writer, speed, number);
        writer.Write('\n');

        // Single writer: the sum is formed here and published with one volatile write for the progress readers.
        Volatile.Write(ref _Bytes, _Bytes + lineBytes);
        Interlocked.Increment(ref _Written);
        return true;
    }

    /// <inheritdoc/>
    public void OnFinish()
    {
        StreamWriter writer = _Writer ?? throw new ObjectDisposedException(nameof(SampleTextListener));
        writer.Flush();
        Volatile.Write(ref _Finished, 1);
    }

    #endregion

    #region IExporterStatistics / IExportByteProgress

    /// <inheritdoc/>
    public int WrittenCount => Volatile.Read(ref _Written);

    /// <inheritdoc/>
    public int SkippedCount => Volatile.Read(ref _Skipped);

    /// <inheritdoc/>
    public int ErrorCount => 0;

    /// <inheritdoc/>
    public bool HasErrors => false;

    /// <inheritdoc/>
    public bool IsFinished => Volatile.Read(ref _Finished) != 0;

    /// <inheritdoc/>
    public long EstimatedOutputBytes => Volatile.Read(ref _Bytes);

    #endregion

    #region Private helpers

    private void _WriteLine(string line)
    {
        StreamWriter writer = _Writer ?? throw new ObjectDisposedException(nameof(SampleTextListener));
        writer.Write(line);
        writer.Write('\n');
        Volatile.Write(ref _Bytes, _Bytes + line.Length + 1);
    }

    // Writes an int and returns its length in characters; an int always fits the number buffer.
    private static int _WriteNumber(StreamWriter writer, int value, Span<char> buffer)
    {
        value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture);
        writer.Write(buffer[..written]);
        return written;
    }

    // Writes a field value and returns its length in characters; a value that does not fit the buffer goes through a string.
    private static int _WriteValue(StreamWriter writer, FieldValue value, Span<char> buffer)
    {
        if (value.TryFormat(buffer, out int written, default, CultureInfo.InvariantCulture))
        {
            writer.Write(buffer[..written]);
            return written;
        }

        string text = value.ToString(null, CultureInfo.InvariantCulture);
        writer.Write(text);
        return text.Length;
    }

    #endregion
}
