// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// The file protocol of the trace cache. For a recipe with the key <c>K</c> and the name <c>N</c> the folder holds
/// <c>N-K.ext</c> (the trace), <c>N-K.files/</c> (side files), <c>N-K.settings.json</c> (the profile settings), and
/// <c>N-K.lock</c> (the writer lock; the empty file stays and is reused). The trace is moved into place last, so a reader that
/// finds it also finds the settings and the side files; readers take no lock. Writers are serialized per recipe across
/// threads and processes by an exclusive handle on the lock file, and wait with a growing pause instead of a fixed sleep.
/// Thread safety: instances are stateless apart from the folder name; all members are thread-safe.
/// </summary>
internal sealed class TraceCache
{
    #region Constants

    /// <summary>First pause while another writer holds the lock.</summary>
    private const int _FirstPauseMilliseconds = 1;

    /// <summary>Longest pause between two attempts to take the lock.</summary>
    private const int _MaxPauseMilliseconds = 64;

    /// <summary>Longest total wait for the lock of another writer.</summary>
    private static readonly TimeSpan _LockTimeout = TimeSpan.FromSeconds(120);

    #endregion

    #region Fields

    private readonly string _Directory;

    #endregion

    #region Lifecycle

    /// <summary>Creates the cache over a folder; the folder is created on the first write.</summary>
    /// <param name="directory">The cache folder (fully qualified).</param>
    internal TraceCache(string directory)
    {
        _Directory = directory;
    }

    #endregion

    #region Public API

    /// <summary>The cache key of a recipe: 16 lower-case hexadecimal characters.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <returns>The key.</returns>
    internal static string KeyOf(TraceRecipe recipe)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"{recipe.Name}\n{recipe.Version}\n{recipe.CanonicalParameters}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
    }

    /// <summary>Returns the trace of a recipe, writing it when it is not in the cache.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock and the write; a cancelled write leaves no file behind.</param>
    /// <returns>The trace.</returns>
    /// <exception cref="TimeoutException">The lock of another writer was not released within two minutes.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal async ValueTask<TraceFile> GetAsync(TraceRecipe recipe, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string stem = Path.Combine(_Directory, recipe.Name + "-" + KeyOf(recipe));
        string tracePath = stem + recipe.Extension;
        string settingsPath = stem + ".settings.json";

        // Fast path: the trace appears atomically and last, so its presence means that everything else is complete.
        if (File.Exists(tracePath) && _TryReadSettings(settingsPath, out IReadOnlyDictionary<string, string>? existing))
        {
            return new TraceFile(tracePath, recipe.FrameCount, existing);
        }

        Directory.CreateDirectory(_Directory);
        using FileStream writerLock = await _AcquireLockAsync(stem + ".lock", cancellationToken).ConfigureAwait(false);

        // Another writer may have finished while this one waited.
        if (File.Exists(tracePath) && _TryReadSettings(settingsPath, out existing))
        {
            return new TraceFile(tracePath, recipe.FrameCount, existing);
        }

        return _Build(recipe, stem, tracePath, settingsPath, cancellationToken);
    }

    #endregion

    #region Private Helpers

    /// <summary>Reads the settings file; <see langword="false"/> when it is missing or damaged (the trace is then rebuilt).</summary>
    private static bool _TryReadSettings(string settingsPath, out IReadOnlyDictionary<string, string> settings)
    {
        settings = FrozenDictionary<string, string>.Empty;
        try
        {
            using FileStream stream = new(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (parsed is null)
            {
                return false;
            }

            settings = parsed.ToFrozenDictionary(StringComparer.Ordinal);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Takes the exclusive lock of one recipe; waits for another writer with a pause from 1 to 64 ms, for two minutes at most.</summary>
    private static async ValueTask<FileStream> _AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int pause = _FirstPauseMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (exception is not (DirectoryNotFoundException or FileNotFoundException)
                && System.Diagnostics.Stopwatch.GetElapsedTime(start) < _LockTimeout)
            {
                // Another writer holds the lock; the condition is waited for, not slept away.
            }
            catch (IOException exception) when (exception is not (DirectoryNotFoundException or FileNotFoundException))
            {
                throw new TimeoutException($"The trace lock '{lockPath}' was not released within {_LockTimeout.TotalSeconds:0} seconds.", exception);
            }

            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = Math.Min(pause * 2, _MaxPauseMilliseconds);
        }
    }

    /// <summary>Writes the trace under the lock: temporary names first, the trace itself last.</summary>
    private static TraceFile _Build(TraceRecipe recipe, string stem, string tracePath, string settingsPath,
        CancellationToken cancellationToken)
    {
        string sideDirectory = stem + ".files";
        string temporaryTrace = tracePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string temporarySettings = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Leftovers of a writer that died half-way are removed before the new write starts.
            _DeleteQuietly(tracePath);
            _DeleteQuietly(settingsPath);
            if (Directory.Exists(sideDirectory))
            {
                Directory.Delete(sideDirectory, recursive: true);
            }

            Directory.CreateDirectory(sideDirectory);
            IReadOnlyDictionary<string, string> settings = recipe.Write(temporaryTrace, sideDirectory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            File.WriteAllText(temporarySettings, JsonSerializer.Serialize(settings));
            File.Move(temporarySettings, settingsPath, overwrite: true);
            File.Move(temporaryTrace, tracePath, overwrite: false);
            return new TraceFile(tracePath, recipe.FrameCount, settings.ToFrozenDictionary(StringComparer.Ordinal));
        }
        catch
        {
            // A failed or cancelled write leaves nothing a reader could take for a trace.
            _DeleteQuietly(temporaryTrace);
            _DeleteQuietly(temporarySettings);
            _DeleteQuietly(tracePath);
            _DeleteQuietly(settingsPath);
            if (Directory.Exists(sideDirectory))
            {
                try
                {
                    Directory.Delete(sideDirectory, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The next writer removes the folder before it starts.
                }
            }

            throw;
        }
    }

    /// <summary>Deletes a file and ignores a failure: leftovers are removed by the next writer.</summary>
    private static void _DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing else can be done for a file that is locked; it is not a trace without the settings and the move.
        }
    }

    #endregion
}
