// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Merge saving, removal of stored values, and access to orphaned values (part of <see cref="SettingsManager"/>). Everything
/// that writes the storage folder runs under the cross-process lock file <c>.ni-settings.lock</c> of that folder.
/// </summary>
public sealed partial class SettingsManager
{
    #region Constants

    /// <summary>File name of the cross-process lock in the storage folder.</summary>
    internal const string LockFileName = ".ni-settings.lock";

    /// <summary>
    /// Longest time to wait for the folder lock: two seconds.
    /// Limit record: value 2000 ms · threat: another process that holds the lock for a long time (a hung or very slow writer)
    /// would otherwise block this caller without end · constant, not configurable · a caller that cannot get the lock fails
    /// closed: nothing is written and the exception tells the user to try again. The operating system releases the lock when
    /// the holding process ends, so a crashed holder does not block for good.
    /// </summary>
    private const int _LockWaitMilliseconds = 2000;

    /// <summary>
    /// First pause between lock attempts; the pause doubles up to <see cref="_MaxLockPauseMilliseconds"/>. An exponential
    /// back-off picks up a briefly held lock within about a millisecond while a lock held longer costs only a few attempts.
    /// </summary>
    private const int _FirstLockPauseMilliseconds = 1;

    /// <summary>Longest pause between lock attempts.</summary>
    private const int _MaxLockPauseMilliseconds = 64;

    /// <summary>Name of the group file stem of the default group (the group with the empty name).</summary>
    private const string _DefaultGroupStem = "default";

    #endregion

    #region Merge saving

    /// <summary>
    /// Merges the named keys into the storage folder: under the lock file <c>.ni-settings.lock</c> each affected group file is
    /// read, the keys are set (a registered setting: its current value; an orphan: its JSON) or removed (a value equal to its
    /// default), and the file is replaced atomically (temporary file, then move). A group file that is left empty is deleted.
    /// Other keys of the files, written by other managers or processes, stay, so two managers or processes that change
    /// different keys never lose each other's changes. Names of several groups are written group by group; an error stops at that
    /// group, the groups before it stay written (and are recorded in the owned-group manifest).
    /// <para>
    /// The lock is retried with a pause that grows from 1 to 64 ms for at most two seconds; then the method throws a
    /// <see cref="PersistenceSettingsException"/> "The settings folder '&lt;path&gt;' is in use by another process; try again.".
    /// </para>
    /// </summary>
    /// <param name="names">Names of registered settings or orphans; a name that is listed twice counts once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A name is neither registered nor an orphan.</exception>
    /// <exception cref="PersistenceSettingsException">No storage path is configured, the lock is not available, or an I/O,
    /// access, or JSON error occurred.</exception>
    public void SaveChanged(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        // Snapshot what to write before the lock is taken, so the folder lock is held only for file work. The values written
        // are the ones at this moment; a change made while the folder lock is awaited is not part of this call. An unknown
        // name fails the whole call before any file is touched.
        List<(string Stem, string Name, JsonNode? Node)> edits = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        _Lock.EnterReadLock();
        try
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
                        $"'{name}' is neither a registered setting nor a stored value."), nameof(names));
                }
            }
        }
        finally
        {
            _Lock.ExitReadLock();
        }

        _MergeIntoFolder(edits);
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
    /// <exception cref="PersistenceSettingsException">No storage path is configured, the lock is not available, or an I/O,
    /// access, or JSON error occurred.</exception>
    public bool Unset(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (StoragePath is null)
        {
            throw PersistenceSettingsException.ForNoStoragePath();
        }

        string stem;
        _Lock.EnterReadLock();
        try
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
        finally
        {
            _Lock.ExitReadLock();
        }

        _MergeIntoFolder([(stem, name, null)]);

        // The file no longer holds the key; now bring the memory in line. The setting may have vanished or become an orphan
        // in the meantime only by a registration, which is harmless: both paths below look the name up again.
        // valueChanged is computed before the reset: only a current value that differs from the default changes by the reset.
        // The events are raised after the lock is released (and SettingsApplied before PendingChanged), like the bulk members.
        bool valueChanged = false;
        bool pendingChanged = false;
        _Lock.EnterWriteLock();
        try
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
        finally
        {
            _Lock.ExitWriteLock();
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

    #endregion

    #region Orphans

    /// <summary>
    /// Snapshot of the values that were loaded for settings that are not registered (for example the settings of a plugin that
    /// is not loaded), ordered by group and name. The JSON values are clones. For tooling and diagnostics: the ordering with
    /// LINQ and the per-value parse allocate, which is accepted because this is not on a read path of the settings.
    /// </summary>
    public IReadOnlyList<OrphanedSettingValue> Orphans
    {
        get
        {
            List<OrphanedSettingValue> result = [];
            _Lock.EnterReadLock();
            try
            {
                foreach (KeyValuePair<string, Dictionary<string, JsonNode>> group in _OrphanedValues.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    foreach (KeyValuePair<string, JsonNode> orphan in group.Value.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                    {
                        // Parse the node text into a document and clone the root, so the result owns its memory.
                        using JsonDocument document = JsonDocument.Parse(orphan.Value.ToJsonString());
                        result.Add(new OrphanedSettingValue(group.Key, orphan.Key, document.RootElement.Clone()));
                    }
                }
            }
            finally
            {
                _Lock.ExitReadLock();
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

        _Lock.EnterWriteLock();
        try
        {
            if (_SettingsByName.ContainsKey(name) || !_TryFindOrphanLocked(name, out string? groupName, out _))
            {
                return false;
            }

            _OrphanedValues[groupName][name] = node;
            return true;
        }
        finally
        {
            _Lock.ExitWriteLock();
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>The group file stem of a group name: <c>default</c> for the default group.</summary>
    private static string _StemOf(string groupName) =>
        groupName.Length == 0
            ? _DefaultGroupStem
            : groupName;

    /// <summary>Gets or creates the JSON object of a group file stem for a save.</summary>
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

    /// <summary>Finds an orphan by setting name in any group; the caller holds a lock.</summary>
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

    /// <summary>Removes an orphan by setting name; empty groups disappear. The caller holds the write lock.</summary>
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

    /// <summary>
    /// Applies edits to the group files under the folder lock: a node sets the key, <see langword="null"/> removes it. A file
    /// that ends up empty is deleted. The owned-group manifest follows the written and deleted files, so a later
    /// <see cref="Save"/> can clean up what this call created.
    /// </summary>
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
            using FolderLock folderLock = FolderLock.Acquire(StoragePath);

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
                    string filePath = Path.Combine(StoragePath, fileName);
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
                        string tempPath = filePath + ".tmp";
                        try
                        {
                            File.WriteAllText(tempPath, obj.ToJsonString(_WriteOptions));
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

    /// <summary>
    /// Brings the owned-group manifest and the set of known group files in line with the files a merge wrote and deleted. After
    /// a failed merge a problem with the manifest itself is swallowed: the error of the merge is the one to report.
    /// </summary>
    private void _RecordOwnership(HashSet<string> written, HashSet<string> deleted, bool mergeCompleted)
    {
        if (StoragePath is null || (written.Count == 0 && deleted.Count == 0))
        {
            return;
        }

        try
        {
            string manifestPath = Path.Combine(StoragePath, SettingsFileAccess.OwnedGroupManifestFileName);
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
        internal static readonly KnownGroupFiles Empty = new(false, FrozenSet<string>.Empty);

        private KnownGroupFiles(bool hasLoaded, FrozenSet<string> files)
        {
            HasLoaded = hasLoaded;
            Files = files;
        }

        /// <summary>Whether Load ran on the manager.</summary>
        internal bool HasLoaded { get; }

        /// <summary>File names (for example <c>tcp.json</c>), compared ignoring case.</summary>
        internal FrozenSet<string> Files { get; }

        /// <summary>The snapshot after a change; the same instance when nothing changes.</summary>
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
    /// too deep, or not an object is an error (<see cref="JsonException"/>): overwriting it would destroy content that
    /// <see cref="Load"/> had skipped with a warning. Fail closed on purpose: a refused merge loses nothing, a silent
    /// overwrite would. The size cap is checked against the length the file system reports and against the bytes actually read,
    /// as in <see cref="Load"/>.
    /// </summary>
    private JsonObject _ReadGroupObject(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        using FileStream stream = SettingsFileAccess.OpenSharedRead(filePath);
        string oversizedMessage = string.Create(CultureInfo.InvariantCulture,
            $"Settings file '{SettingsFileAccess.SafeFileLabel(filePath)}' exceeds {MaxConfigFileBytes} bytes.");
        if (MaxConfigFileBytes > 0 && stream.Length > MaxConfigFileBytes)
        {
            throw new JsonException(oversizedMessage);
        }

        // The cap also holds for the bytes actually read, as in Load: the reported length can be wrong for a file that grows
        // after the check or for a file the system reports no useful length for, and the whole file is parsed into memory.
        using BoundedReadStream? bounded = MaxConfigFileBytes > 0 ? new BoundedReadStream(stream, MaxConfigFileBytes) : null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bounded ?? (Stream)stream, nodeOptions: null,
                documentOptions: new JsonDocumentOptions { MaxDepth = EffectiveJsonMaxDepth(MaxJsonDepth) });
        }
        catch (JsonException) when (bounded is { Exceeded: true })
        {
            // Cut at the cap: report the size, not the syntax error the cut produced.
            throw new JsonException(oversizedMessage);
        }

        if (bounded is { Exceeded: true })
        {
            throw new JsonException(oversizedMessage);
        }

        return root as JsonObject
            ?? throw new JsonException($"Settings file '{SettingsFileAccess.SafeFileLabel(filePath)}' does not contain a JSON object.");
    }

    #endregion

    #region Folder lock

    /// <summary>
    /// The cross-process lock of a settings folder: an exclusive handle on <c>.ni-settings.lock</c>. The file stays in the
    /// folder (deleting it would race with a process that is about to open it). Disposing releases the lock.
    /// </summary>
    private sealed class FolderLock : IDisposable
    {
        private readonly FileStream _Stream;

        private FolderLock(FileStream stream)
        {
            _Stream = stream;
        }

        /// <summary>
        /// Takes the lock, retrying while another holder has it: the pause grows from 1 to 64 ms, for at most two seconds.
        /// </summary>
        /// <param name="folder">The settings folder; it must exist.</param>
        /// <returns>The held lock.</returns>
        /// <exception cref="PersistenceSettingsException">The lock was not available in time.</exception>
        internal static FolderLock Acquire(string folder)
        {
            string path = Path.Combine(folder, LockFileName);
            long start = Stopwatch.GetTimestamp();
            int pause = _FirstLockPauseMilliseconds;
            while (true)
            {
                try
                {
                    return new FolderLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException ex) when (ex is not (DirectoryNotFoundException or FileNotFoundException))
                {
                    // A sharing violation is an IOException on every platform; anything else that persists ends the same way.
                    // A missing folder is excluded from the retry (waiting cannot fix it) and propagates at once; an access
                    // denial is not an IOException and propagates too.
                    if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= _LockWaitMilliseconds)
                    {
                        throw PersistenceSettingsException.ForLocked(folder);
                    }
                }

                Thread.Sleep(pause);
                pause = Math.Min(pause * 2, _MaxLockPauseMilliseconds);
            }
        }

        /// <summary>Releases the lock.</summary>
        public void Dispose() => _Stream.Dispose();
    }

    #endregion
}
