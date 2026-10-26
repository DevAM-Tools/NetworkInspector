// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Collects the named contributions of one plugin by kind and the commit actions of its builder. The loader runs the commit
/// actions after every configure call of every tier returned, so a plugin that fails anywhere publishes nothing.
/// <para>
/// Thread safety: not thread-safe. It is used on the loading thread; deferred stack callbacks that fire later during
/// loading run on that thread too.
/// </para>
/// </summary>
public sealed class ContributionRecorder
{
    #region Types

    // Open -> Committed (every commit action returned) or Open -> Discarded (the plugin failed). Discarded is final: a failed
    // plugin must never become visible again, even when a late callback still reaches the recorder.
    private enum RecorderState : byte
    {
        /// <summary>The plugin is being configured; entries and commit actions are accepted.</summary>
        Open,

        /// <summary>All commit actions ran; the plugin's contributions are published.</summary>
        Committed,

        /// <summary>The plugin failed; entries and commit actions were dropped.</summary>
        Discarded,
    }

    #endregion

    #region Fields

    // Single-threaded by contract (see the class summary), so neither collection nor the state needs synchronization.
    private readonly Dictionary<ContributionKind, List<ContributionEntry>> _Entries = [];

    // Kept in registration order: the order in which a plugin's builder registered its commits is the order they run.
    private readonly List<Action> _CommitActions = [];

    private RecorderState _State;

    #endregion

    #region Lifecycle

    /// <summary>Creates a recorder for one plugin.</summary>
    /// <param name="pluginName">Name of the plugin the contributions belong to.</param>
    /// <exception cref="ArgumentException"><paramref name="pluginName"/> is <see langword="null"/>, empty, or white space.</exception>
    public ContributionRecorder(string pluginName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        PluginName = pluginName;
    }

    #endregion

    #region Public API

    /// <summary>Plugin the contributions belong to.</summary>
    public string PluginName { get; }

    /// <summary>
    /// Records one named contribution. Accepted while the recorder is open and after the commit (late deferred stack callbacks
    /// still name what they register); ignored after a discard, because a failed plugin contributes nothing.
    /// </summary>
    /// <param name="kind">Kind of the contribution.</param>
    /// <param name="name">Name of the contribution; not empty.</param>
    /// <param name="description">Short description; may be empty, never <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public void Record(ContributionKind kind, string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);

        // A discarded recorder drops silently instead of throwing: the late callers are deferred stack callbacks of the
        // probe builder, which fire inside another plugin's commit and must not make that plugin fail.
        if (_State == RecorderState.Discarded)
        {
            return;
        }

        // Entries are grouped by kind; the list is created on first use because most plugins use only a few kinds.
        if (!_Entries.TryGetValue(kind, out List<ContributionEntry>? list))
        {
            list = [];
            _Entries[kind] = list;
        }

        list.Add(new ContributionEntry(name, description));
    }

    /// <summary>
    /// Registers work that runs when the loader commits the plugin, after every configure call returned, in registration
    /// order. A throwing action fails the plugin.
    /// </summary>
    /// <param name="commit">The work to run at commit.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commit"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The recorder is already committed or discarded.</exception>
    public void OnCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        // Unlike Record, a late action is refused loudly: it could never run, so accepting it would hide a lost contribution.
        if (_State != RecorderState.Open)
        {
            throw new InvalidOperationException("Commit actions can only be added while the plugin is configured.");
        }

        _CommitActions.Add(commit);
    }

    #endregion

    #region Internal API

    /// <summary>
    /// Runs the commit actions in order. A throwing action leaves the state open so that the loader can discard; a second
    /// commit after success throws. Actions that already ran before the throwing one are not undone here: an action that
    /// publishes must leave nothing behind when it throws itself (the registry's commit does).
    /// </summary>
    /// <exception cref="InvalidOperationException">The recorder was already committed or discarded.</exception>
    internal void Commit()
    {
        if (_State != RecorderState.Open)
        {
            throw new InvalidOperationException("The recorder was already committed or discarded.");
        }

        // The state changes only after the last action returned. While actions run the recorder stays Open, so an exception
        // reaches the loader (which discards the plugin) and never leaves a half-committed recorder marked as committed.
        foreach (Action commit in _CommitActions)
        {
            commit();
        }

        _State = RecorderState.Committed;
    }

    /// <summary>Drops every entry and action; later <see cref="Record"/> calls are ignored. Idempotent.</summary>
    internal void Discard()
    {
        _Entries.Clear();
        _CommitActions.Clear();
        _State = RecorderState.Discarded;
    }

    /// <summary>Frozen copy of the entries by kind; empty after a discard.</summary>
    /// <returns>The entries of every kind that has at least one.</returns>
    internal IReadOnlyDictionary<ContributionKind, IReadOnlyList<ContributionEntry>> Snapshot()
    {
        // Copies instead of sharing the live lists: the plugin record that receives the result is immutable and may be read
        // from other threads, while this recorder stays writable after the commit (late deferred callbacks may still record).
        Dictionary<ContributionKind, IReadOnlyList<ContributionEntry>> copy = new(_Entries.Count);
        foreach (KeyValuePair<ContributionKind, List<ContributionEntry>> pair in _Entries)
        {
            copy[pair.Key] = [.. pair.Value];
        }

        return copy.ToFrozenDictionary();
    }

    #endregion
}
