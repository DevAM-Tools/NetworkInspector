// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>Apply and reset for a set of named settings, and the pending-change event (part of <see cref="SettingsManager"/>).</summary>
public sealed partial class SettingsManager
{
    #region Events

    /// <summary>
    /// Raised after the pending value of a setting changed (it was set, reset, or returned to its default). The event is
    /// raised outside the manager lock and outside the setting lock, so a handler may read the manager; a handler that throws
    /// passes its exception to the caller that changed the value.
    /// </summary>
    public event EventHandler<SettingPendingChangedEventArgs>? PendingChanged;

    /// <summary>Raises <see cref="PendingChanged"/>; called by <see cref="Setting"/> after its lock is released.</summary>
    /// <param name="name">Name of the setting whose pending value changed.</param>
    internal void OnPendingChanged(string name) =>
        PendingChanged?.Invoke(this, new SettingPendingChangedEventArgs(name));

    #endregion

    #region Per-key operations

    /// <summary>
    /// Applies the pending values of the named settings only and raises <see cref="SettingsApplied"/> once when at least one
    /// value changed.
    /// </summary>
    /// <param name="names">Names of registered settings; a name that is listed twice counts once.</param>
    /// <returns>The names whose current value changed, in the order given.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> or one of its elements is <see langword="null"/>
    /// (checked before anything is applied).</exception>
    /// <exception cref="ArgumentException">A name is not registered (checked before anything is applied).</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Load"/> is in progress (raised by the setting).</exception>
    public IReadOnlyList<string> ApplyChanges(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        // Same shape as the all-settings ApplyChanges: apply under the write lock, raise the event after it is released.
        List<string> changed = [];
        _Lock.EnterWriteLock();
        try
        {
            // Resolve every name first so that an unknown name leaves all pending values as they are (all-or-nothing on the
            // names; the apply itself is then per setting).
            List<Setting> selected = _ResolveLocked(names);
            foreach (Setting setting in selected)
            {
                if (setting.IsDirty && setting.Apply())
                {
                    changed.Add(setting.Name);
                }
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }

        // Fire the event outside the lock to avoid holding it during subscriber callbacks.
        if (changed.Count > 0)
        {
            SettingsApplied?.Invoke(this, new SettingsAppliedEventArgs(changed));
        }

        return changed;
    }

    /// <summary>
    /// Resets the pending values of the named settings to their current values and raises <see cref="PendingChanged"/> for
    /// every setting whose pending value changed.
    /// </summary>
    /// <param name="names">Names of registered settings; a name that is listed twice counts once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> or one of its elements is <see langword="null"/>
    /// (checked before anything is reset).</exception>
    /// <exception cref="ArgumentException">A name is not registered (checked before anything is reset).</exception>
    public void ResetChanges(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<string> changed = [];
        _Lock.EnterWriteLock();
        try
        {
            foreach (Setting setting in _ResolveLocked(names))
            {
                if (setting.ResetWithoutEvent())
                {
                    changed.Add(setting.Name);
                }
            }
        }
        finally
        {
            _Lock.ExitWriteLock();
        }

        _RaisePendingChanged(changed);
    }

    #endregion

    #region Private Helpers

    /// <summary>Resolves names to settings (duplicates removed, order kept); the caller holds a lock.</summary>
    private List<Setting> _ResolveLocked(IEnumerable<string> names)
    {
        List<Setting> selected = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string name in names)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (!_SettingsByName.TryGetValue(name, out Setting? setting))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"No setting named '{name}' is registered."),
                    nameof(names));
            }

            if (seen.Add(name))
            {
                selected.Add(setting);
            }
        }

        return selected;
    }

    /// <summary>
    /// Raises <see cref="PendingChanged"/> for each name, after the manager lock was released. A handler that throws ends the
    /// loop: the names after it are not notified, and the exception reaches the caller of the bulk operation.
    /// </summary>
    private void _RaisePendingChanged(List<string> names)
    {
        foreach (string name in names)
        {
            OnPendingChanged(name);
        }
    }

    #endregion
}
