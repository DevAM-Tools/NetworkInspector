// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// Builds each recipe once per cache folder and returns the cached file afterwards. Writers are serialized per recipe across
/// threads and processes by an exclusive lock file; readers of an existing file take no lock, because the file appears
/// atomically and last. Test projects that run in parallel share <see cref="Default"/>; a test that needs its own folder
/// (for example to count files) creates a kit on a folder of its own. Thread safety: all members are thread-safe.
/// </summary>
/// <example>
/// <code>
/// TraceFile trace = await TraceKit.Default.GetAsync(new CanSignalsRecipe(300), cancellationToken);
/// </code>
/// </example>
public sealed class TraceKit
{
    #region Fields

    private readonly TraceCache _Cache;

    #endregion

    #region Lifecycle

    /// <summary>Creates a kit over a cache folder; the folder is created when the first trace is written.</summary>
    /// <param name="cacheDirectory">A fully qualified folder path.</param>
    /// <exception cref="ArgumentException">The folder is empty or not fully qualified.</exception>
    public TraceKit(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (!Path.IsPathFullyQualified(cacheDirectory))
        {
            throw new ArgumentException($"The cache folder '{cacheDirectory}' is not fully qualified.", nameof(cacheDirectory));
        }

        CacheDirectory = cacheDirectory;
        _Cache = new TraceCache(cacheDirectory);
    }

    #endregion

    #region Properties

    /// <summary>The kit on <c>&lt;temp&gt;/ni-trace-cache/v1</c>, shared by all test projects of a machine.</summary>
    public static TraceKit Default { get; } = new(Path.Combine(Path.GetTempPath(), "ni-trace-cache", "v1"));

    /// <summary>The cache folder of this kit.</summary>
    public string CacheDirectory { get; }

    #endregion

    #region Public API

    /// <summary>Returns the trace of <paramref name="recipe"/>, writing it when it is not in the cache.</summary>
    /// <param name="recipe">The recipe.</param>
    /// <param name="cancellationToken">Cancels the wait and the write; a cancelled write leaves no trace file behind.</param>
    /// <returns>The trace file with its frame count and profile settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recipe"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    /// <exception cref="TimeoutException">Another writer held the lock of this recipe for more than two minutes.</exception>
    public ValueTask<TraceFile> GetAsync(TraceRecipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return _Cache.GetAsync(recipe, cancellationToken);
    }

    #endregion
}
