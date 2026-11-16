// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// The output of a single-file exporter: one entry, <see cref="ExportEntryName.Main"/>, of an <see cref="IExportTarget"/>, opened
/// on the first write or the first stream request, so an export that never writes opens nothing. The factories keep the former
/// shortcuts: <see cref="File"/> (a lazy file with a 4 MiB buffer), <see cref="FromStream"/> (a caller stream that stays open),
/// <see cref="Stdout"/> (standard output, closed at the end); <see cref="FromTarget"/> takes any host target.
/// <para>
/// Ownership: the output disposes the entry stream it opened; whether that closes anything is the rule of the target (a file
/// target closes its file, a non-owning stream target only flushes the caller's stream). Exporters create the output through the
/// builder, take the stream with <see cref="TryGetOrCreateUnderlyingStream"/>, and dispose the output exactly once on finish.
/// </para>
/// <para>
/// <b>Thread safety:</b> Not thread-safe. An output belongs to one exporter, which uses it from one thread at a time.
/// </para>
/// </summary>
/// <seealso cref="IExportTarget"/>
public sealed class ExportOutput : IDisposable
{
    #region Configuration and state

    /// <summary>The target that hands out the one entry.</summary>
    private readonly IExportTarget _Target;

    /// <summary>The open entry stream; <see langword="null"/> until the first write or stream request.</summary>
    private Stream? _Stream;

    /// <summary>Creates an output over <paramref name="target"/>.</summary>
    /// <param name="target">The target; already validated by the factory.</param>
    private ExportOutput(IExportTarget target) => _Target = target;

    #endregion

    #region Factories

    /// <summary>
    /// Creates a file-backed output with a 4 MiB write buffer. The file is not created until the first write; if no data is
    /// ever written, no file is created on disk.
    /// </summary>
    /// <param name="path">Absolute or relative path to the output file. Must not be null or empty.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the specified file.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid path characters.</exception>
    public static ExportOutput File(string path) => new(new FileExportTarget(path));

    /// <summary>Creates an output that writes to an existing stream. The caller keeps ownership and the stream stays open.</summary>
    /// <param name="stream">The target stream; it may be non-seekable.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the given stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    public static ExportOutput FromStream(Stream stream) => new(new StreamExportTarget(stream));

    /// <summary>Creates an output that writes to stdout.</summary>
    /// <returns>A new <see cref="ExportOutput"/> writing to the standard output stream, closed when the output is disposed.</returns>
    public static ExportOutput Stdout() => new(StreamExportTarget.Stdout());

    /// <summary>Creates an output that opens <see cref="ExportEntryName.Main"/> of a host target on its first write.</summary>
    /// <param name="target">The host's target.</param>
    /// <returns>A new <see cref="ExportOutput"/> over <paramref name="target"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    public static ExportOutput FromTarget(IExportTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ExportOutput(target);
    }

    #endregion

    #region Writing

    /// <summary>
    /// Total bytes accepted via <see cref="Write"/> so far (includes data still in write buffers). Does not include bytes
    /// written to the stream of <see cref="TryGetOrCreateUnderlyingStream"/> directly.
    /// </summary>
    public long AcceptedBytes { get; private set; }

    /// <summary>Writes bytes to the output; the first call opens the entry.</summary>
    /// <param name="data">The data to write.</param>
    /// <exception cref="IOException">The target refused the entry (the message is the target's reason) or the stream failed.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (!TryGetOrCreateUnderlyingStream(out Stream? stream, out string failure))
        {
            throw new IOException(failure);
        }

        stream.Write(data);
        AcceptedBytes += data.Length;
    }

    /// <summary>
    /// Flushes any buffered data to the output. For an output that was never opened, nothing is flushed and nothing is created.
    /// </summary>
    public void Flush() => _Stream?.Flush();

    #endregion

    #region Stream access

    /// <summary>
    /// Returns the entry stream, opening it on the first call, and reports a refused open without an exception. A refusal stays
    /// refused: the targets accept one open, which matches the exporters' rule that a failed start is not retried.
    /// </summary>
    /// <param name="stream">The write-only entry stream on success.</param>
    /// <param name="failure">The target's reason on failure; empty on success.</param>
    /// <returns><see langword="true"/> when <paramref name="stream"/> is set.</returns>
    internal bool TryGetOrCreateUnderlyingStream([NotNullWhen(true)] out Stream? stream, out string failure)
    {
        if (_Stream is not null)
        {
            stream = _Stream;
            failure = string.Empty;
            return true;
        }

        if (!_Target.TryOpenWrite(ExportEntryName.Main, out stream, out failure))
        {
            return false;
        }

        _Stream = stream;
        return true;
    }

    /// <summary>
    /// Returns the entry stream when it is open, and never opens it: for a file output no file is created by this call.
    /// </summary>
    /// <returns>The open stream, or <see langword="null"/> when nothing was opened yet.</returns>
    internal Stream? TryGetExistingStream() => _Stream;

    #endregion

    #region Disposal

    /// <summary>
    /// Disposes the entry stream when it was opened; for a file target that flushes and closes the file, for a non-owning
    /// stream target it flushes the caller's stream and leaves it open. Nothing happens when the entry was never opened.
    /// </summary>
    public void Dispose()
    {
        Stream? stream = _Stream;
        _Stream = null;
        stream?.Dispose();
    }

    #endregion
}
