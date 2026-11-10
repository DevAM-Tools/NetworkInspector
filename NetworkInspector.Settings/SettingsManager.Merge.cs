// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Merge saving, removal of stored values, and access to orphaned values (part of <see cref="SettingsManager"/>). Everything
/// that writes the storage folder runs under the cross-process lock file of that folder (<see cref="SettingsStorageLayout.LockFileName"/>).
/// </summary>
public sealed partial class SettingsManager
{
    #region Lock retry pauses

    /// <summary>
    /// First pause between lock attempts; the pause doubles up to <see cref="_MaxLockPauseMilliseconds"/>. An exponential
    /// back-off picks up a briefly held lock within about a millisecond while a lock held longer costs only a few attempts.
    /// Internal tuning, not a limit: it only chooses how often the lock is tried; the longest wait is
    /// <see cref="SettingsLimits.FolderLockTimeout"/>.
    /// </summary>
    private const int _FirstLockPauseMilliseconds = 1;

    /// <summary>Longest pause between lock attempts. Internal tuning, not a limit.</summary>
    private const int _MaxLockPauseMilliseconds = 64;

    #endregion

    #region Merge saving

    /// <summary>
    /// Merges the named keys into the storage folder: under the lock file (<see cref="SettingsStorageLayout.LockFileName"/>) each affected group file is
    /// read, the keys are set (a registered setting: its current value; an orphan: its JSON) or removed (a value equal to its
    /// default), and the file is replaced atomically (temporary file, then move). A group file that is left empty is deleted.
    /// Other keys of the files, written by other managers or processes, stay, so two managers or processes that change
    /// different keys never lose each other's changes. Names of several groups are written group by group; an error stops at that
    /// group, the groups before it stay written (and are recorded in the owned-group manifest).
    /// <para>
    /// The lock is retried with a pause that grows from 1 to 64 ms for at most <see cref="SettingsLimitValues.FolderLockTimeout"/>
    /// (2 s by default); then the method throws a <see cref="PersistenceSettingsException"/> that names the folder, the limit, and its key.
    /// The wait blocks the calling thread; <see cref="SaveChangedAsync"/> waits without blocking one.
    /// </para>
    /// </summary>
    /// <param name="names">Names of registered settings or orphans; a name that is listed twice counts once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is neither registered nor an orphan.</exception>
    /// <exception cref="PersistenceSettingsException">No storage path is configured, the lock is not available, a file that the
    /// merge reads is too large, nested too deep, repeats a key, or is not valid JSON (the file stays as it is), or an I/O or access
    /// error occurred.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public void SaveChanged(IReadOnlyCollection<string> names)
    {
        List<(string Stem, string Name, JsonNode? Node)> edits = _SnapshotEdits(names);
        _MergeIntoFolder(edits);
    }

    /// <summary>
    /// Like <see cref="SaveChanged"/>, but the wait for the folder lock does not block a thread and honors
    /// <paramref name="cancellationToken"/>. The file work after the lock is short and stays synchronous.
    /// </summary>
    /// <param name="names">Names of registered settings or orphans; a name that is listed twice counts once.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock; once the lock is held the merge completes.</param>
    /// <returns>A task that completes when the keys are written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is neither registered nor an orphan.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled while the lock was awaited; nothing was written.
    /// </exception>
    /// <exception cref="PersistenceSettingsException">As <see cref="SaveChanged"/>.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    /// <remarks>
    /// Why: a hosted application (a user interface, a server) must not park a thread for as long as
    /// <see cref="SettingsLimits.FolderLockTimeout"/>, or without end when that limit is off. Thread safety: as
    /// <see cref="SaveChanged"/>.
    /// </remarks>
    public async ValueTask SaveChangedAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default)
    {
        List<(string Stem, string Name, JsonNode? Node)> edits = _SnapshotEdits(names);
        await _MergeIntoFolderAsync(edits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the stored value of <paramref name="name"/>: a registered setting returns to its default (current and pending
    /// value; <see cref="SettingsApplied"/> is raised when the current value changed), an orphan is dropped. The key is
    /// removed from its group file like <see cref="SaveChanged"/> does. The file is changed first, so a failure leaves the
    /// manager as it was.
    /// </summary>
    /// <param name="name">Name of a registered setting or an orphan.</param>
    /// <returns><see langword="false"/> when the name is neither registered nor an orphan (nothing is written).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="PersistenceSettingsException">No storage path is configured, the lock is not available, a file that the
    /// merge reads cannot be used (too large, nested too deep, repeats a key, or not valid JSON), or an I/O or access error
    /// occurred.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public bool Unset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        string stem;
        _ThrowIfDisposed();
        lock (_Lock)
        {
            if (_SettingsByName.TryGetValue(name, out Setting? registered))
            {
                stem = _StemOf(registered.GroupName);
            }
            else if (_TryFindOrphanLocked(name, out string? orphanGroup, out _))
            {
                stem = _StemOf(orphanGroup);
            }
            else
            {
                return false;
            }
        }

        _MergeIntoFolder([(stem, name, null)]);

        // The file no longer holds the key; now bring the memory in line. The setting may have vanished or become an orphan
        // in the meantime only by a registration, which is harmless: both paths below look the name up again.
        // valueChanged is computed before the reset: only a current value that differs from the default changes by the reset.
        // The events are raised after the lock is released (and SettingsApplied before PendingChanged), like the bulk members.
        bool valueChanged = false;
        bool pendingChanged = false;
        lock (_Lock)
        {
            if (_SettingsByName.TryGetValue(name, out Setting? setting))
            {
                valueChanged = setting.Value != setting.DefaultValue;
                pendingChanged = setting.ResetToDefaultWithoutEvent();
            }
            else
            {
                _RemoveOrphanLocked(name);
            }
        }

        if (valueChanged)
        {
            SettingsApplied?.Invoke(this, new SettingsAppliedEventArgs([name]));
        }

        if (pendingChanged)
        {
            OnPendingChanged(name);
        }

        return true;
    }

    /// <summary>
    /// Takes the snapshot that a merge writes: for each name the group file stem and the node to set, or <see langword="null"/>
    /// to remove the key.
    /// </summary>
    /// <param name="names">Names of registered settings or orphans; a name that is listed twice counts once.</param>
    /// <returns>One edit per distinct name, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> or one of its elements is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is neither registered nor an orphan (nothing is touched).</exception>
    /// <exception cref="PersistenceSettingsException">No storage path is configured.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    /// <remarks>
    /// Snapshot before the folder lock is taken, so the folder lock is held only for file work. The values written are the ones at
    /// this moment; a change made while the folder lock is awaited is not part of the call. An unknown name fails the whole call
    /// before any file is touched.
    /// </remarks>
    private List<(string Stem, string Name, JsonNode? Node)> _SnapshotEdits(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        List<(string Stem, string Name, JsonNode? Node)> edits = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        _ThrowIfDisposed();
        lock (_Lock)
        {
            foreach (string name in names)
            {
                ArgumentNullException.ThrowIfNull(name);
                if (!seen.Add(name))
                {
                    continue;
                }

                if (_SettingsByName.TryGetValue(name, out Setting? setting))
                {
                    // Sparse: a value equal to its default is removed, not stored.
                    edits.Add((_StemOf(setting.GroupName), name, setting.Value == setting.DefaultValue
                        ? null
                        : SettingValueJson.ToNode(setting.Value)));
                }
                else if (_TryFindOrphanLocked(name, out string? groupName, out JsonNode? orphan))
                {
                    // Clone: a JsonNode can have only one parent, and the orphan table keeps its own node.
                    edits.Add((_StemOf(groupName), name, orphan.DeepClone()));
                }
                else
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                        $"'{SettingValidation.Quote(name)}' is neither a registered setting nor a stored value."), nameof(names));
                }
            }
        }

        return edits;
    }

    #endregion

    #region Orphans

    /// <summary>
    /// Snapshot of the values that were loaded for settings that are not registered (for example the settings of a component that
    /// is not loaded), ordered by group and name. The JSON values are clones. For tooling and diagnostics: the ordering with
    /// LINQ and the per-value parse allocate, which is accepted because this is not on a read path of the settings. The lock
    /// covers only the copy of the nodes; the ordering and the conversion run after it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public IReadOnlyList<OrphanedSettingValue> Orphans
    {
        get
        {
            List<(string Group, string Name, JsonNode Node)> copied = [];
            _ThrowIfDisposed();
            lock (_Lock)
            {
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> group in _OrphanedValues)
                {
                    foreach (KeyValuePair<string, JsonNode> orphan in group.Value)
                    {
                        // Clone: a node has one parent and the orphan table keeps its own.
                        copied.Add((group.Key, orphan.Key, orphan.Value.DeepClone()));
                    }
                }
            }

            copied.Sort(static (left, right) =>
            {
                int byGroup = string.CompareOrdinal(left.Group, right.Group);
                return byGroup != 0
                    ? byGroup
                    : string.CompareOrdinal(left.Name, right.Name);
            });

            List<OrphanedSettingValue> result = new(copied.Count);
            foreach ((string group, string name, JsonNode node) in copied)
            {
                // Parse the node text into a document and clone the root, so the result owns its memory.
                using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
                result.Add(new OrphanedSettingValue(group, name, document.RootElement.Clone()));
            }

            return result;
        }
    }

    /// <summary>
    /// Replaces the JSON value of an existing orphan. Write it to the folder with <see cref="SaveChanged"/>.
    /// </summary>
    /// <param name="name">Name of the orphaned value.</param>
    /// <param name="value">The new JSON value.</param>
    /// <returns><see langword="false"/> for a registered name, an unknown name, or a JSON null; the orphan is then unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    public bool TrySetOrphanValue(string name, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(name);

        // A JSON null has no node form and is not a setting value, so it is refused like an undefined element.
        JsonNode? node;
        try
        {
            node = value.ValueKind == JsonValueKind.Undefined
                ? null
                : JsonNode.Parse(value.GetRawText());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            node = null;
        }

        if (node is null)
        {
            return false;
        }

        _ThrowIfDisposed();
        lock (_Lock)
        {
            if (_SettingsByName.ContainsKey(name) || !_TryFindOrphanLocked(name, out string? groupName, out _))
            {
                return false;
            }

            _OrphanedValues[groupName][name] = node;
            return true;
        }
    }

    /// <summary>Finds an orphan by setting name in any group; the caller holds the lock.</summary>
    /// <param name="name">The setting name to look for.</param>
    /// <param name="groupName">The group that holds the orphan when the method returns <see langword="true"/>.</param>
    /// <param name="node">The stored JSON node (not a clone) when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when an orphan of this name exists.</returns>
    private bool _TryFindOrphanLocked(string name, [NotNullWhen(true)] out string? groupName, [NotNullWhen(true)] out JsonNode? node)
    {
        foreach (KeyValuePair<string, Dictionary<string, JsonNode>> group in _OrphanedValues)
        {
            if (group.Value.TryGetValue(name, out node))
            {
                groupName = group.Key;
                return true;
            }
        }

        groupName = null;
        node = null;
        return false;
    }

    /// <summary>Removes an orphan by setting name; empty groups disappear. The caller holds the lock.</summary>
    /// <param name="name">The setting name of the orphan; an unknown name changes nothing.</param>
    private void _RemoveOrphanLocked(string name)
    {
        if (!_TryFindOrphanLocked(name, out string? groupName, out _))
        {
            return;
        }

        Dictionary<string, JsonNode> group = _OrphanedValues[groupName];
        group.Remove(name);
        if (group.Count == 0)
        {
            _OrphanedValues.Remove(groupName);
        }
    }

    #endregion

    #region Group files and the storage folder

    /// <summary>The group file stem of a group name: <c>default</c> for the default group.</summary>
    /// <param name="groupName">The group name; empty for the default group.</param>
    /// <returns>The file name without extension.</returns>
    /// <remarks>Sync: <see cref="SettingNames.DefaultGroupStem"/>; <c>Load</c> maps the stem back to the group <c>""</c>.</remarks>
    private static string _StemOf(string groupName) =>
        groupName.Length == 0
            ? SettingNames.DefaultGroupStem
            : groupName;

    /// <summary>Gets or creates the JSON object of a group file stem for a save.</summary>
    /// <param name="groups">The objects by file stem; the new object is added here.</param>
    /// <param name="groupName">The group; empty for the default group.</param>
    /// <returns>The object that collects the keys of the group's file.</returns>
    private static JsonObject _GroupObject(Dictionary<string, JsonObject> groups, string groupName)
    {
        string stem = _StemOf(groupName);
        if (!groups.TryGetValue(stem, out JsonObject? obj))
        {
            obj = [];
            groups[stem] = obj;
        }

        return obj;
    }

    /// <summary>
    /// Creates the storage folder when it does not exist. A path the file system cannot use at all (empty, null character,
    /// unsupported form) is reported as a <see cref="PersistenceSettingsException"/> like every other file-system failure of
    /// the persistence members, not as the caller-bug exception the framework throws for it.
    /// </summary>
    /// <param name="storagePath">The configured storage path.</param>
    /// <exception cref="PersistenceSettingsException">The path is invalid.</exception>
    private static void _CreateStorageFolder(string storagePath)
    {
        try
        {
            _ = Directory.CreateDirectory(storagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw PersistenceSettingsException.ForInvalidPath(ex);
        }
    }

    /// <summary>Writes a group object to a new file as indented UTF-8 without a byte-order mark.</summary>
    /// <param name="path">The temporary file to create; it must not exist (its name carries a unique id).</param>
    /// <param name="group">The object to write.</param>
    /// <exception cref="IOException">The file cannot be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder refuses the write.</exception>
    /// <remarks>
    /// Perf: the writer streams into the file, so no string or byte array of the whole file exists besides the object itself.
    /// Rejected: <c>ToJsonString</c> and <c>File.WriteAllText</c>, which hold the whole file twice. The encoder is the default
    /// one, as before: non-ASCII characters are written as escapes.
    /// </remarks>
    private static void _WriteGroupFile(string path, JsonObject group)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        group.WriteTo(writer);
    }

    #endregion

    #region Merge into the group files

    /// <summary>
    /// Applies edits to the group files under the folder lock: a node sets the key, <see langword="null"/> removes it. A file
    /// that ends up empty is deleted. The owned-group manifest follows the written and deleted files, so a later
    /// <see cref="Save"/> can clean up what this call created.
    /// </summary>
    /// <param name="edits">The snapshot of <see cref="_SnapshotEdits"/>.</param>
    /// <exception cref="PersistenceSettingsException">No storage path, no lock in time, an unusable file, or an I/O or access error.</exception>
    private void _MergeIntoFolder(IReadOnlyList<(string Stem, string Name, JsonNode? Node)> edits)
    {
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        // Removing keys that are not stored changes nothing; then no lock file, folder, or manifest should appear either.
        if (_AllEditsAreNoOps(edits))
        {
            return;
        }

        try
        {
            _CreateStorageFolder(StoragePath);
            using FolderLock folderLock = FolderLock.Acquire(StoragePath, _PersistLayout.LockFileName, _LiveFolderLockTimeout.Value);
            _MergeUnderLock(StoragePath, edits);
        }
        catch (JsonException ex)
        {
            throw PersistenceSettingsException.ForJson(ex);
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PersistenceSettingsException.ForAccessDenied(ex);
        }
    }

    /// <summary>The awaiting counterpart of <see cref="_MergeIntoFolder"/>: the lock is awaited without blocking a thread.</summary>
    /// <param name="edits">The snapshot of <see cref="_SnapshotEdits"/>.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <exception cref="OperationCanceledException">The wait was canceled; nothing was written.</exception>
    /// <exception cref="PersistenceSettingsException">As <see cref="_MergeIntoFolder"/>.</exception>
    private async ValueTask _MergeIntoFolderAsync(IReadOnlyList<(string Stem, string Name, JsonNode? Node)> edits, CancellationToken cancellationToken)
    {
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        if (_AllEditsAreNoOps(edits))
        {
            return;
        }

        try
        {
            _CreateStorageFolder(StoragePath);
            using FolderLock folderLock = await FolderLock
                .AcquireAsync(StoragePath, _PersistLayout.LockFileName, _LiveFolderLockTimeout.Value, cancellationToken)
                .ConfigureAwait(false);
            _MergeUnderLock(StoragePath, edits);
        }
        catch (JsonException ex)
        {
            throw PersistenceSettingsException.ForJson(ex);
        }
        catch (IOException ex)
        {
            throw PersistenceSettingsException.ForIo(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw PersistenceSettingsException.ForAccessDenied(ex);
        }
    }

    /// <summary>The merge itself; the caller holds the folder lock.</summary>
    /// <param name="storagePath">The storage folder; it exists.</param>
    /// <param name="edits">The snapshot of <see cref="_SnapshotEdits"/>.</param>
    /// <exception cref="JsonException">A group file cannot be used; it stays as it is.</exception>
    /// <exception cref="IOException">A file cannot be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder refuses an access.</exception>
    private void _MergeUnderLock(string storagePath, IReadOnlyList<(string Stem, string Name, JsonNode? Node)> edits)
    {
        HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> deleted = new(StringComparer.OrdinalIgnoreCase);
        bool completed = false;
        try
        {
            foreach (IGrouping<string, (string Stem, string Name, JsonNode? Node)> group in edits.GroupBy(static edit => edit.Stem, StringComparer.Ordinal))
            {
                // Per group file: read the current content (other managers may have changed it), apply this call's keys, and
                // write back. Reading the file again under the lock is what makes this a merge instead of an overwrite.
                string fileName = group.Key + ".json";
                string filePath = Path.Combine(storagePath, fileName);
                JsonObject obj = _ReadGroupObject(filePath);
                foreach ((string _, string name, JsonNode? node) in group)
                {
                    if (node is null)
                    {
                        obj.Remove(name);
                    }
                    else
                    {
                        // Clone: the edit's node may only have one parent, and the caller's edits stay untouched.
                        obj[name] = node.DeepClone();
                    }
                }

                if (obj.Count == 0)
                {
                    File.Delete(filePath);
                    deleted.Add(fileName);
                }
                else
                {
                    // Atomic replace: readers see the old or the new file, never half of one.
                    string tempPath = SettingsFileAccess.NewTempPath(filePath);
                    try
                    {
                        _WriteGroupFile(tempPath, obj);
                        SettingsFileAccess.ReplaceFile(tempPath, filePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A failed replace must not leave the half of it behind; the original error is the one to report.
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                        {
                            // The next save overwrites or removes a stale temporary file of this name.
                        }

                        throw;
                    }

                    written.Add(fileName);
                }
            }

            completed = true;
        }
        finally
        {
            // The files are changed one by one; whatever was changed before an error is recorded, so a later Save can clean up.
            _RecordOwnership(written, deleted, completed);
        }
    }

    /// <summary>
    /// Brings the owned-group manifest and the set of known group files in line with the files a merge wrote and deleted. After
    /// a failed merge a problem with the manifest itself is swallowed: the error of the merge is the one to report.
    /// </summary>
    /// <param name="written">The file names the merge wrote.</param>
    /// <param name="deleted">The file names the merge deleted.</param>
    /// <param name="mergeCompleted">Whether the merge finished without an error.</param>
    private void _RecordOwnership(HashSet<string> written, HashSet<string> deleted, bool mergeCompleted)
    {
        if (StoragePath is null || (written.Count == 0 && deleted.Count == 0))
        {
            return;
        }

        try
        {
            string manifestPath = Path.Combine(StoragePath, _PersistLayout.OwnedManifestFileName);
            HashSet<string> owned = _ReadOwnedGroupFiles(manifestPath);
            owned.UnionWith(written);
            owned.ExceptWith(deleted);
            _WriteOwnedGroupFiles(manifestPath, owned);
        }
        catch (Exception ex) when (!mergeCompleted && ex is IOException or UnauthorizedAccessException)
        {
            // The merge already failed; its error reaches the caller. The next save rewrites the manifest.
        }

        _RecordKnownGroupFiles(loaded: false, written, deleted);
    }

    /// <summary>
    /// Adds and removes group file names in the known-files snapshot; <paramref name="loaded"/> marks that Load ran (it never
    /// resets the mark). Lock-free: a new immutable snapshot replaces the old one with a compare-exchange; a lost race retries
    /// on the newer snapshot, so no change of a concurrent Load, Save, or SaveChanged is lost.
    /// </summary>
    /// <param name="loaded">Whether this change comes from a <see cref="Load"/>.</param>
    /// <param name="added">The file names to add.</param>
    /// <param name="removed">The file names to remove.</param>
    private void _RecordKnownGroupFiles(bool loaded, IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed)
    {
        KnownGroupFiles current;
        KnownGroupFiles next;
        do
        {
            current = Volatile.Read(ref _KnownGroupFiles);
            next = current.With(loaded, added, removed);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _KnownGroupFiles, next, current), current));
    }

    /// <summary>
    /// Immutable snapshot of the group files a manager knows and whether it loaded. Replaced as a whole, so a reader always sees
    /// the mark and the names of the same moment.
    /// </summary>
    private sealed class KnownGroupFiles
    {
        /// <summary>The state of a manager that has not loaded and knows no file.</summary>
        internal static readonly KnownGroupFiles Empty = new(false, FrozenSet<string>.Empty);

        /// <summary>Creates a snapshot.</summary>
        /// <param name="hasLoaded">Whether Load ran.</param>
        /// <param name="files">The known file names, compared ignoring case.</param>
        private KnownGroupFiles(bool hasLoaded, FrozenSet<string> files)
        {
            HasLoaded = hasLoaded;
            Files = files;
        }

        /// <summary>Whether Load ran on the manager.</summary>
        internal bool HasLoaded { get; }

        /// <summary>File names (for example <c>app.json</c>), compared ignoring case.</summary>
        internal FrozenSet<string> Files { get; }

        /// <summary>The snapshot after a change; the same instance when nothing changes.</summary>
        /// <param name="loaded">Whether this change comes from a Load.</param>
        /// <param name="added">The file names to add.</param>
        /// <param name="removed">The file names to remove.</param>
        /// <returns>The new snapshot, or this one when nothing changes.</returns>
        internal KnownGroupFiles With(bool loaded, IReadOnlyCollection<string> added, IReadOnlyCollection<string> removed)
        {
            bool hasLoaded = HasLoaded || loaded;
            if (hasLoaded == HasLoaded && added.Count == 0 && removed.Count == 0)
            {
                return this;
            }

            HashSet<string> files = new(Files, StringComparer.OrdinalIgnoreCase);
            files.UnionWith(added);
            files.ExceptWith(removed);
            return new KnownGroupFiles(hasLoaded, files.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Whether the edits would change no file: only removals, each of a key that is not in its file (or whose file does not
    /// exist). The check reads without the lock; a file that cannot be read counts as "changes something" so that the real merge
    /// reports the problem. Purpose: an unset of a value that is not stored must not create the folder, the lock file, or the
    /// manifest as a side effect. A stale answer (another process adds the key right after the check) is harmless: the removal
    /// is then simply ordered before that process's write.
    /// </summary>
    /// <param name="edits">The snapshot of <see cref="_SnapshotEdits"/>.</param>
    /// <returns><see langword="true"/> when the merge would write and delete nothing.</returns>
    private bool _AllEditsAreNoOps(IReadOnlyList<(string Stem, string Name, JsonNode? Node)> edits)
    {
        foreach (IGrouping<string, (string Stem, string Name, JsonNode? Node)> group in edits.GroupBy(static edit => edit.Stem, StringComparer.Ordinal))
        {
            if (group.Any(static edit => edit.Node is not null))
            {
                return false;
            }

            string filePath = Path.Combine(StoragePath ?? string.Empty, group.Key + ".json");
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                JsonObject obj = _ReadGroupObject(filePath);
                if (group.Any(edit => obj.ContainsKey(edit.Name)))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a group file as a JSON object for merging; a missing file is an empty object. A file that is too large, nested
    /// too deep, repeats a key, or is not an object is an error (<see cref="JsonException"/>): overwriting it would destroy content that
    /// <see cref="Load"/> had skipped with a warning. Fail closed on purpose: a refused merge loses nothing, a silent
    /// overwrite would. The size cap is checked against the length the file system reports and against the bytes actually read,
    /// as in <see cref="Load"/>.
    /// </summary>
    /// <param name="filePath">The group file.</param>
    /// <returns>The root object of the file, or an empty object when the file does not exist.</returns>
    /// <exception cref="JsonException">The file cannot be used; the message names the limit that was hit, or the problem.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be accessed.</exception>
    private JsonObject _ReadGroupObject(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        using FileStream stream = SettingsFileAccess.OpenSharedRead(filePath);
        string label = SettingsFileAccess.SafeFileLabel(filePath);
        string oversizedMessage = _OversizedText(label);
        long maxBytes = _LiveMaxFileSize.Ceiling;
        if (stream.Length > maxBytes)
        {
            throw new JsonException(oversizedMessage);
        }

        // The cap also holds for the bytes actually read, as in Load: the reported length can be wrong for a file that grows
        // after the check or for a file the system reports no useful length for, and the whole file is parsed into memory.
        using BoundedReadStream bounded = new(stream, maxBytes);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bounded, nodeOptions: null,
                documentOptions: new JsonDocumentOptions { MaxDepth = EffectiveJsonMaxDepth(_LiveMaxJsonDepth.Value) });
        }
        catch (JsonException) when (bounded.Exceeded)
        {
            // Cut at the cap: report the size, not the syntax error the cut produced.
            throw new JsonException(oversizedMessage);
        }
        catch (JsonException ex)
        {
            // The parser does not say that the nesting was the cause; the probe counts it, so the message names the limit. The
            // probe reads the file, so it runs here and not in an exception filter.
            if (_IsTooDeep(filePath))
            {
                throw new JsonException(_TooDeepText(label), ex);
            }

            throw;
        }

        if (bounded.Exceeded)
        {
            throw new JsonException(oversizedMessage);
        }

        if (root is not JsonObject obj)
        {
            throw new JsonException(Invariant($"Settings file '{SettingValidation.Quote(label)}' does not contain a JSON object."));
        }

        // The object indexes its names on first use and throws for a repeated name; force that here, where a JsonException reaches
        // the caller as a persistence failure and the file stays as it is. Load has the same guard and only warns.
        try
        {
            _ = obj.Count;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new JsonException(
                Invariant($"Settings file '{SettingValidation.Quote(label)}' contains invalid JSON: ")
                + "a property name occurs twice or cannot be decoded.",
                ex);
        }

        return obj;
    }

    #endregion

    #region Folder lock

    /// <summary>
    /// The cross-process lock of a settings folder: an exclusive handle on the lock file of the layout. The file stays in the
    /// folder (deleting it would race with a process that is about to open it). Disposing releases the lock.
    /// </summary>
    private sealed class FolderLock : IDisposable
    {
        /// <summary>The exclusive handle on the lock file; the lock is held while it is open.</summary>
        private readonly FileStream _Stream;

        /// <summary>Wraps the handle of an acquired lock.</summary>
        /// <param name="stream">The open lock file.</param>
        private FolderLock(FileStream stream)
        {
            _Stream = stream;
        }

        /// <summary>
        /// Takes the lock, retrying while another holder has it: the pause grows from 1 to 64 ms, for at most
        /// <paramref name="timeout"/>.
        /// </summary>
        /// <param name="folder">The settings folder; it must exist.</param>
        /// <param name="lockFileName">The lock file name of the layout of the manager.</param>
        /// <param name="timeout">A value of <see cref="SettingsLimits.FolderLockTimeout"/>; <see cref="LimitValue.Off"/> waits without end.</param>
        /// <returns>The held lock.</returns>
        /// <exception cref="PersistenceSettingsException">The lock was not available in time; the message names the limit.</exception>
        /// <exception cref="IOException">The lock file cannot be opened for a reason other than a holder (a full disk, a bad path).</exception>
        internal static FolderLock Acquire(string folder, string lockFileName, LimitValue timeout)
        {
            string path = Path.Combine(folder, lockFileName);
            long start = Stopwatch.GetTimestamp();

            // Ceiling is long.MaxValue for off, so the comparison below never succeeds and the wait has no end by choice.
            long timeoutMilliseconds = timeout.Ceiling;
            int pause = _FirstLockPauseMilliseconds;
            while (true)
            {
                try
                {
                    return new FolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when (_IsContention(ex))
                {
                    if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= timeoutMilliseconds)
                    {
                        throw PersistenceSettingsException.ForLocked(folder, timeout);
                    }
                }

                Thread.Sleep(pause);
                pause = Math.Min(pause * 2, _MaxLockPauseMilliseconds);
            }
        }

        /// <summary>
        /// Takes the lock like <see cref="Acquire"/>, but waits with a timer instead of a blocked thread and stops at cancellation.
        /// </summary>
        /// <param name="folder">The settings folder; it must exist.</param>
        /// <param name="lockFileName">The lock file name of the layout of the manager.</param>
        /// <param name="timeout">A value of <see cref="SettingsLimits.FolderLockTimeout"/>; <see cref="LimitValue.Off"/> waits without end.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>The held lock.</returns>
        /// <exception cref="PersistenceSettingsException">The lock was not available in time; the message names the limit.</exception>
        /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
        /// <exception cref="IOException">The lock file cannot be opened for a reason other than a holder.</exception>
        internal static async ValueTask<FolderLock> AcquireAsync(
            string folder,
            string lockFileName,
            LimitValue timeout,
            CancellationToken cancellationToken)
        {
            string path = Path.Combine(folder, lockFileName);
            long start = Stopwatch.GetTimestamp();
            long timeoutMilliseconds = timeout.Ceiling;
            int pause = _FirstLockPauseMilliseconds;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return new FolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when (_IsContention(ex))
                {
                    if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= timeoutMilliseconds)
                    {
                        throw PersistenceSettingsException.ForLocked(folder, timeout);
                    }
                }

                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
                pause = Math.Min(pause * 2, _MaxLockPauseMilliseconds);
            }
        }

        /// <summary>Whether an error of the open means that another holder has the lock file.</summary>
        /// <param name="exception">The error of the open.</param>
        /// <returns><see langword="true"/> for a sharing or lock violation; every other I/O error is not contention.</returns>
        /// <remarks>
        /// A full disk, a path that is too long, or a bad drive does not pass by waiting: retrying would report the wrong cause
        /// ("in use by another process") and, with the limit off, never end. Windows reports ERROR_SHARING_VIOLATION (32) or
        /// ERROR_LOCK_VIOLATION (33) in the low word of the HRESULT; Unix reports EWOULDBLOCK (11 on Linux, 35 on macOS) as the raw
        /// error number. Sync: the open of <see cref="Acquire"/> with <see cref="FileShare.None"/>.
        /// </remarks>
        private static bool _IsContention(IOException exception)
        {
            int code = exception.HResult;
            return OperatingSystem.IsWindows()
                ? (code & 0xFFFF) is 32 or 33
                : code is 11 or 35;
        }

        /// <summary>Releases the lock.</summary>
        public void Dispose() => _Stream.Dispose();
    }

    #endregion
}
