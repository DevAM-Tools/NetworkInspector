// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Zero-allocation read-only view over a <see cref="SettingGroup"/>.
/// <para>
/// When the compile-time type is this struct, accessors can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IReadOnlySettingGroup"/>
/// so the JIT does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IReadOnlySettingGroup"/>, store it in that
/// interface type, or pass it to a non-generic parameter of that type. Those conversions
/// box. Prefer this struct (or a generic constraint) at call sites.
/// </para>
/// </summary>
public readonly struct ReadOnlySettingGroupView : IReadOnlySettingGroup
{
    #region Fields

    private readonly SettingGroup _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlySettingGroupView(SettingGroup owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _Owner = owner;
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public string Name => _Owner.Name;

    /// <inheritdoc/>
    public string UiName => _Owner.UiName;

    /// <inheritdoc/>
    public string? Description => _Owner.Description;

    /// <inheritdoc/>
    public bool IsDefaultGroup => _Owner.IsDefaultGroup;

    /// <inheritdoc/>
    public int SettingCount => _Owner.SettingCount;

    /// <inheritdoc/>
    public IReadOnlyList<ReadOnlySettingView> Settings =>
        ReadOnlySettingView.Wrap(_Owner.CopySettings());

    #endregion
}
