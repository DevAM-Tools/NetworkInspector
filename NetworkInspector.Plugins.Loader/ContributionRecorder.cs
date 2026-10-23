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

    private enum RecorderState : byte
    {
        Open,
        Committed,
        Discarded,
    }

    #endregion

    #region Fields

    private readonly Dictionary<ContributionKind, List<ContributionEntry>> _Entries = [];

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
    public void Record(ContributionKind kind, string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        if (_State == RecorderState.Discarded)
        {
            return;
        }

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
    /// <exception cref="InvalidOperationException">The recorder is already committed or discarded.</exception>
    public void OnCommit(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
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
    /// commit after success throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">The recorder was already committed or discarded.</exception>
    internal void Commit()
    {
        if (_State != RecorderState.Open)
        {
            throw new InvalidOperationException("The recorder was already committed or discarded.");
        }

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
        Dictionary<ContributionKind, IReadOnlyList<ContributionEntry>> copy = new(_Entries.Count);
        foreach (KeyValuePair<ContributionKind, List<ContributionEntry>> pair in _Entries)
        {
            copy[pair.Key] = [.. pair.Value];
        }

        return copy.ToFrozenDictionary();
    }

    #endregion
}
