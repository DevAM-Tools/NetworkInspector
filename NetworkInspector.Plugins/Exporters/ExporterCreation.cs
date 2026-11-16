// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins;

/// <summary>
/// Creates the <see cref="ExporterCreation{TListener}"/> of a registration. The factories sit on this non-generic class because a
/// static member on a generic type is refused by rule CA1000; the type argument is the listener and is given at the call:
/// <c>ExporterCreation.ForStreams&lt;IPacketListener&gt;(TryCreate)</c>.
/// <para>
/// <b>Thread safety:</b> thread-safe; the class holds no state.
/// </para>
/// </summary>
/// <seealso cref="ExporterCreation{TListener}"/>
public static class ExporterCreation
{
    /// <summary>
    /// A creation for a format that writes forward through the streams of a host target, which is every format except one whose
    /// library opens the file itself.
    /// </summary>
    /// <typeparam name="TListener">The Core listener type.</typeparam>
    /// <param name="create">Creates the listener from a <see cref="StreamExportRequest"/>.</param>
    /// <returns>A creation that holds exactly <paramref name="create"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
    public static ExporterCreation<TListener> ForStreams<TListener>(StreamExporterCreate<TListener> create)
        where TListener : class
    {
        ArgumentNullException.ThrowIfNull(create);
        return new ExporterCreation<TListener>(create, null);
    }

    /// <summary>
    /// A creation for a format whose library opens the file itself (DuckDB): the host passes a prepared path instead of a target.
    /// </summary>
    /// <typeparam name="TListener">The Core listener type.</typeparam>
    /// <param name="create">Creates the listener from a <see cref="FileExportRequest"/>.</param>
    /// <returns>A creation that holds exactly <paramref name="create"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
    public static ExporterCreation<TListener> ForFile<TListener>(FileExporterCreate<TListener> create)
        where TListener : class
    {
        ArgumentNullException.ThrowIfNull(create);
        return new ExporterCreation<TListener>(null, create);
    }
}

/// <summary>
/// How an export format receives its output: exactly one of a target of named streams
/// (<see cref="ExporterCreation.ForStreams{TListener}"/>, every format that writes forward) or a prepared file path
/// (<see cref="ExporterCreation.ForFile{TListener}"/>, a format whose library opens the file itself, such as DuckDB). The
/// constructor is not public, so a registration cannot hold both or neither. Immutable; thread-safe.
/// </summary>
/// <typeparam name="TListener">The Core listener type.</typeparam>
/// <seealso cref="ExporterRegistration{TListener}"/>
public sealed class ExporterCreation<TListener>
    where TListener : class
{
    #region Configuration and state

    /// <summary>The stream creation, or <see langword="null"/> for a file creation.</summary>
    private readonly StreamExporterCreate<TListener>? _ForStreams;

    /// <summary>The file creation, or <see langword="null"/> for a stream creation.</summary>
    private readonly FileExporterCreate<TListener>? _ForFile;

    /// <summary>Creates a creation of one kind; reachable only through <see cref="ExporterCreation"/>.</summary>
    /// <param name="forStreams">The stream creation, or <see langword="null"/>.</param>
    /// <param name="forFile">The file creation, or <see langword="null"/>; exactly one of the two is set.</param>
    internal ExporterCreation(StreamExporterCreate<TListener>? forStreams, FileExporterCreate<TListener>? forFile)
    {
        _ForStreams = forStreams;
        _ForFile = forFile;
    }

    #endregion

    #region Kind

    /// <summary>Whether the format receives a prepared file path (and so needs a file extension) instead of a target.</summary>
    public bool WritesFile => _ForFile is not null;

    /// <summary>Gets the stream creation.</summary>
    /// <param name="create">The creation when this holds one; <see langword="null"/> for a file creation.</param>
    /// <returns><see langword="true"/> for a stream creation.</returns>
    public bool TryGetForStreams([NotNullWhen(true)] out StreamExporterCreate<TListener>? create)
    {
        create = _ForStreams;
        return create is not null;
    }

    /// <summary>Gets the file creation.</summary>
    /// <param name="create">The creation when this holds one; <see langword="null"/> for a stream creation.</param>
    /// <returns><see langword="true"/> for a file creation.</returns>
    public bool TryGetForFile([NotNullWhen(true)] out FileExporterCreate<TListener>? create)
    {
        create = _ForFile;
        return create is not null;
    }

    #endregion
}
