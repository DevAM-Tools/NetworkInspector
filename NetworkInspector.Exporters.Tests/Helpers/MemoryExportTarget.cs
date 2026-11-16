// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// An in-memory <see cref="IExportTarget"/> for tests: records every entry in open order with its bytes and whether it was
/// closed, and can refuse entries. The streams are <see cref="ForwardOnlyStream"/>s (the strictest case) or seekable memory
/// streams.
/// <para>
/// <b>Thread safety:</b> thread-safe: a <see cref="Lock"/> guards the entry list because the Parquet writer thread opens field
/// entries while the caller's thread opened the first ones.
/// </para>
/// </summary>
/// <seealso cref="ForwardOnlyStream"/>
internal sealed class MemoryExportTarget : IExportTarget
{
    #region Configuration and state

    /// <summary>Whether the streams seek; <see langword="false"/> hands out <see cref="ForwardOnlyStream"/>s.</summary>
    private readonly bool _Seekable;

    /// <summary>Returns the refusal reason for an entry, or <see langword="null"/> to open it.</summary>
    private readonly Func<ExportEntryName, string?>? _Refuse;

    /// <summary>Guards <see cref="_Entries"/>.</summary>
    private readonly Lock _Gate = new();

    /// <summary>The opened entries in open order.</summary>
    private readonly List<Entry> _Entries = [];

    /// <summary>Creates the target.</summary>
    /// <param name="seekable"><see langword="false"/> hands out <see cref="ForwardOnlyStream"/>s; <see langword="true"/> seekable ones.</param>
    /// <param name="refuse">Returns the refusal reason for an entry, or <see langword="null"/> to open it.</param>
    internal MemoryExportTarget(bool seekable = false, Func<ExportEntryName, string?>? refuse = null)
    {
        _Seekable = seekable;
        _Refuse = refuse;
    }

    /// <summary>One opened entry: its name, its stream, and how to read its bytes and its closed state.</summary>
    /// <param name="Name">The entry text, empty for the main entry.</param>
    /// <param name="Stream">The stream handed to the exporter.</param>
    /// <param name="ToBytes">Reads the written bytes.</param>
    /// <param name="IsClosed">Whether the exporter disposed the stream.</param>
    private sealed record Entry(string Name, Stream Stream, Func<byte[]> ToBytes, Func<bool> IsClosed);

    #endregion

    #region Recorded entries

    /// <summary>The text of each opened entry in open order; empty text for the main entry.</summary>
    internal IReadOnlyList<string> EntryNames
    {
        get
        {
            lock (_Gate)
            {
                return _Entries.Select(static entry => entry.Name).ToArray();
            }
        }
    }

    /// <summary>The bytes of a closed entry.</summary>
    /// <param name="entryName">The entry text, empty for the main entry.</param>
    /// <returns>The written bytes.</returns>
    /// <exception cref="InvalidOperationException">The entry is unknown or still open.</exception>
    internal byte[] Bytes(string entryName)
    {
        Entry entry = _Find(entryName);
        if (!entry.IsClosed())
        {
            throw new InvalidOperationException(Invariant($"Entry '{entryName}' is still open."));
        }

        return entry.ToBytes();
    }

    /// <summary>Whether the exporter closed an entry.</summary>
    /// <param name="entryName">The entry text, empty for the main entry.</param>
    /// <returns><see langword="true"/> when the entry's stream was disposed.</returns>
    /// <exception cref="InvalidOperationException">The entry is unknown.</exception>
    internal bool IsClosed(string entryName) => _Find(entryName).IsClosed();

    /// <summary>Finds an opened entry by its text.</summary>
    /// <param name="entryName">The entry text.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="InvalidOperationException">No entry of that text was opened.</exception>
    private Entry _Find(string entryName)
    {
        lock (_Gate)
        {
            foreach (Entry entry in _Entries)
            {
                if (string.Equals(entry.Name, entryName, StringComparison.Ordinal))
                {
                    return entry;
                }
            }
        }

        throw new InvalidOperationException(Invariant($"Entry '{entryName}' was never opened."));
    }

    #endregion

    #region Opening

    /// <summary>Opens an entry unless it is refused or was opened before; a refused or repeated open records nothing.</summary>
    /// <param name="name">The entry.</param>
    /// <param name="stream">The entry stream on success.</param>
    /// <param name="failure">The refusal reason or "already opened".</param>
    /// <returns><see langword="true"/> when <paramref name="stream"/> is set.</returns>
    public bool TryOpenWrite(ExportEntryName name, [NotNullWhen(true)] out Stream? stream, out string failure)
    {
        string? refusal = _Refuse?.Invoke(name);
        if (refusal is not null)
        {
            stream = null;
            failure = refusal;
            return false;
        }

        lock (_Gate)
        {
            foreach (Entry existing in _Entries)
            {
                if (string.Equals(existing.Name, name.Text, StringComparison.Ordinal))
                {
                    stream = null;
                    failure = Invariant($"Entry '{name}' was already opened.");
                    return false;
                }
            }

            Entry entry;
            if (_Seekable)
            {
                RecordingMemoryStream seekable = new();
                entry = new Entry(name.Text, seekable, seekable.ToArray, () => seekable.IsDisposed);
            }
            else
            {
                ForwardOnlyStream forward = new();
                entry = new Entry(name.Text, forward, forward.ToArray, () => forward.IsDisposed);
            }

            _Entries.Add(entry);
            stream = entry.Stream;
            failure = string.Empty;
            return true;
        }
    }

    #endregion

    #region Seekable stream

    /// <summary>A memory stream that records its dispose; its bytes stay readable afterwards.</summary>
    private sealed class RecordingMemoryStream : MemoryStream
    {
        /// <summary>Whether the stream was disposed.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    #endregion
}
