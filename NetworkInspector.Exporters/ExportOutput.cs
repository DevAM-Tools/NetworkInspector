// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Exporters;

/// <summary>
/// Abstraction for export output targets (file, stream, stdout).
/// File outputs use a 4 MiB buffer for efficient large-block I/O.
/// </summary>
public abstract class ExportOutput : IDisposable
{
    /// <summary>Default buffer size for file output (4 MiB).</summary>
    private const int DefaultFileBufferSize = 4 * 1024 * 1024;

    /// <summary>Creates a file-backed output with a 4 MiB write buffer.
    /// The file is not created until the first write; if no data is ever written,
    /// no file is created on disk.
    /// </summary>
    /// <param name="path">Absolute or relative path to the output file.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the specified file.</returns>
    public static ExportOutput File(string path) =>
        new LazyFileExportOutput(path, DefaultFileBufferSize);

    /// <summary>Creates an output that writes to an existing stream.</summary>
    /// <param name="stream">The target stream. Caller retains ownership.</param>
    /// <returns>A new <see cref="ExportOutput"/> that writes to the given stream.</returns>
    public static ExportOutput FromStream(Stream stream) =>
        new StreamExportOutput(stream, ownsStream: false);

    /// <summary>Creates an output that writes to stdout.</summary>
    /// <returns>A new <see cref="ExportOutput"/> writing to the standard output stream.</returns>
    public static ExportOutput Stdout() =>
        new StreamExportOutput(Console.OpenStandardOutput(), ownsStream: true);

    /// <summary>Writes bytes to the output.</summary>
    /// <param name="data">The data to write.</param>
    public abstract void Write(ReadOnlySpan<byte> data);

    /// <summary>Flushes any buffered data to the output.</summary>
    public abstract void Flush();

    /// <summary>Gets the underlying stream, creating it if needed.</summary>
    /// <remarks>
    /// This method replaces the former <c>UnderlyingStream</c> property. The name
    /// makes the side-effect explicit — on a <see cref="LazyFileExportOutput"/>, the first
    /// call materialises a new file on disk (create/truncate). Callers that only need to
    /// query whether the stream already exists must call
    /// <see cref="TryGetExistingStream"/> instead.
    /// </remarks>
    internal abstract Stream? GetOrCreateUnderlyingStream();

    /// <summary>
    /// Returns the underlying stream if it has already been materialised, or <c>null</c>
    /// if the stream has not yet been created (i.e. no data has been written yet).
    /// Unlike <see cref="GetOrCreateUnderlyingStream"/>, this method never creates a file
    /// or allocates a stream as a side-effect.
    /// </summary>
    internal abstract Stream? TryGetExistingStream();

    /// <summary>Releases resources used by this output.</summary>
    /// <param name="disposing">True if called from Dispose, false if from finalizer.</param>
    protected abstract void Dispose(bool disposing);

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Lazy file-backed <see cref="ExportOutput"/> implementation.
/// Opens (creates) the file only on the first write so that exporters that never
/// write any data leave no file on disk.
/// </summary>
internal sealed class LazyFileExportOutput : ExportOutput
{
    private readonly string _Path;
    private readonly int _BufferSize;
    private Stream? _Stream;

    /// <summary>Creates a lazy file output.</summary>
    /// <param name="path">Target file path.</param>
    /// <param name="bufferSize">Write buffer size in bytes.</param>
    internal LazyFileExportOutput(string path, int bufferSize)
    {
        _Path = path;
        _BufferSize = bufferSize;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        _Stream ??= new BufferedStream(
            new FileStream(_Path, FileMode.Create, FileAccess.Write, FileShare.None),
            _BufferSize);
        _Stream.Write(data);
    }

    /// <inheritdoc/>
    public override void Flush() => _Stream?.Flush();

    /// <inheritdoc/>
    /// <remarks>Materialises the underlying <see cref="FileStream"/> on first call.</remarks>
    internal override Stream? GetOrCreateUnderlyingStream()
    {
        _Stream ??= new BufferedStream(
            new FileStream(_Path, FileMode.Create, FileAccess.Write, FileShare.None),
            _BufferSize);
        return _Stream;
    }

    /// <inheritdoc/>
    internal override Stream? TryGetExistingStream() => _Stream;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _Stream?.Dispose();
            _Stream = null;
        }
    }
}

/// <summary>Stream-backed <see cref="ExportOutput"/> implementation.</summary>
internal sealed class StreamExportOutput : ExportOutput
{
    private readonly Stream _Stream;
    private readonly bool _OwnsStream;

    /// <summary>Creates a stream-backed output.</summary>
    /// <param name="stream">Target stream.</param>
    /// <param name="ownsStream">If true, the stream is disposed when this output is disposed.</param>
    internal StreamExportOutput(Stream stream, bool ownsStream)
    {
        _Stream = stream;
        _OwnsStream = ownsStream;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data) => _Stream.Write(data);

    /// <inheritdoc/>
    public override void Flush() => _Stream.Flush();

    /// <inheritdoc/>
    internal override Stream? GetOrCreateUnderlyingStream() => _Stream;

    /// <inheritdoc/>
    internal override Stream? TryGetExistingStream() => _Stream;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && _OwnsStream)
        {
            _Stream.Dispose();
        }
    }
}
