// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>
/// A target with one entry, <see cref="ExportEntryName.Main"/>, written to a stream the caller supplies. A non-owning target
/// hands out a <see cref="NonClosingStream"/>, so the exporter's dispose of its entry leaves the caller's stream open; an owning
/// target (stdout) hands out the stream itself, which the exporter closes.
/// <para>
/// <b>Thread safety:</b> thread-safe like <see cref="FileExportTarget"/>; the stream itself is used by one exporter at a time.
/// </para>
/// </summary>
/// <seealso cref="IExportTarget"/>
/// <seealso cref="FileExportTarget"/>
public sealed class StreamExportTarget : IExportTarget
{
    #region Configuration and state

    /// <summary>The caller's stream.</summary>
    private readonly Stream _Stream;

    /// <summary>Whether the entry closes <see cref="_Stream"/> when the exporter disposes it.</summary>
    private readonly bool _OwnsStream;

    /// <summary>0 until the entry was opened, then 1.</summary>
    private int _Opened;

    /// <summary>Creates the target over <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream; it may be non-seekable.</param>
    /// <param name="ownsStream"><see langword="true"/> when the exporter may close the stream with its entry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    public StreamExportTarget(Stream stream, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _Stream = stream;
        _OwnsStream = ownsStream;
    }

    /// <summary>Creates an owning target over the process's standard output.</summary>
    /// <returns>A target whose entry closes standard output when the exporter finishes.</returns>
    public static StreamExportTarget Stdout() => new(Console.OpenStandardOutput(), ownsStream: true);

    #endregion

    #region Opening

    /// <summary>Hands out the stream on the first call; every later call, and every named entry, is refused.</summary>
    /// <param name="name">Must be <see cref="ExportEntryName.Main"/>.</param>
    /// <param name="stream">The caller's stream, or a <see cref="NonClosingStream"/> over it when the target does not own it.</param>
    /// <param name="failure">The reason on failure: a named entry or a second open.</param>
    /// <returns><see langword="true"/> when <paramref name="stream"/> is set.</returns>
    public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
    {
        stream = null;
        if (!name.IsMain)
        {
            failure = Invariant($"A stream target holds one output; entry '{name}' needs a DirectoryExportTarget.");
            return false;
        }

        if (Interlocked.Exchange(ref _Opened, 1) != 0)
        {
            failure = "The output was already opened.";
            return false;
        }

        stream = _OwnsStream
            ? _Stream
            : new NonClosingStream(_Stream);
        failure = string.Empty;
        return true;
    }

    #endregion
}
