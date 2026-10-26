// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// The description of one deterministic trace. The key (the first 16 hexadecimal characters of the SHA-256 over name, version,
/// and the canonical parameter text) names the cache file, so equal recipes share a file and a changed parameter or version
/// gets a new one. Any change of the bytes a recipe writes needs a new <see cref="Version"/>. Recipes are immutable; every
/// member is thread-safe.
/// </summary>
public abstract class TraceRecipe
{
    #region Constants

    // Limit: frames per recipe, 1 to 10,000,000. Threat: none from input; test support only, so this protects the test
    // machine from a typo (a multi-gigabyte file in the temp folder). Constant: it is not a host-derived or user setting.
    // Recipes that are meant to be larger (the large random trace) pass their own maximum to CheckCount.
    /// <summary>Largest frame or packet count of a recipe, so that a test cannot ask for a file of unbounded size by accident.</summary>
    public const int MaxFrames = 10_000_000;

    /// <summary>
    /// Base time of every trace: 2026-01-01T00:00:00Z in nanoseconds since the Unix epoch. A fixed instant (never the clock)
    /// keeps the written bytes identical on every run, which the cache key and the byte-for-byte tests depend on.
    /// </summary>
    public const long BaseNanos = 1_767_225_600L * 1_000_000_000L;

    #endregion

    #region Properties

    /// <summary>Short file name stem, lower case with dashes (for example <c>mixed-ethernet</c>).</summary>
    public abstract string Name { get; }

    /// <summary>Version of the byte layout; raise it whenever the bytes written for the same parameters change.</summary>
    public abstract int Version { get; }

    /// <summary>File extension with the dot (for example <c>.pcapng</c>).</summary>
    public abstract string Extension { get; }

    /// <summary>Number of frames the trace holds.</summary>
    public abstract int FrameCount { get; }

    /// <summary>
    /// Every parameter as invariant text in a fixed order, for example <c>count=200;seed=1</c>; it is part of the cache key.
    /// </summary>
    public abstract string CanonicalParameters { get; }

    #endregion

    #region Protected API

    /// <summary>Checks a frame or packet count against 1 and an upper limit; recipe constructors call it.</summary>
    /// <param name="count">The count.</param>
    /// <param name="parameterName">The constructor parameter, for the exception.</param>
    /// <param name="maximum">The largest accepted count.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is below 1 or above <paramref name="maximum"/>.</exception>
    protected static void CheckCount(int count, string parameterName, int maximum = MaxFrames)
    {
        if (count < 1 || count > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, count, $"The count must be between 1 and {maximum}.");
        }
    }

    /// <summary>
    /// Writes the trace and its side files. Called by <see cref="TraceKit"/> under the writer lock of the recipe.
    /// </summary>
    /// <param name="tracePath">Where to write the trace (a temporary name; the kit moves it into place when it is complete).</param>
    /// <param name="sideFileDirectory">
    /// The final folder for side files (it exists and is empty); settings that point at side files use paths inside it.
    /// </param>
    /// <param name="cancellationToken">Cancels the write; the kit then removes everything that was written.</param>
    /// <returns>The profile settings a test needs for the trace to decode; empty for none.</returns>
    protected internal abstract IReadOnlyDictionary<string, string> Write(string tracePath, string sideFileDirectory,
        CancellationToken cancellationToken);

    #endregion
}
