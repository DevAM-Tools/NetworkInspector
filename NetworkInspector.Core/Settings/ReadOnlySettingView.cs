// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Settings;

/// <summary>
/// Zero-allocation read-only view over a <see cref="Setting"/>.
/// <para>
/// When the compile-time type is this struct, accessors can inline to the owner.
/// Consume it through generic methods constrained to <see cref="IReadOnlySetting"/>
/// so the JIT does not box.
/// </para>
/// <para>
/// Warning: do not cast this struct to <see cref="IReadOnlySetting"/>, store it in that
/// interface type, or pass it to a non-generic parameter of that type. Those conversions
/// box. Prefer this struct (or a generic constraint) at call sites.
/// </para>
/// </summary>
public readonly struct ReadOnlySettingView : IReadOnlySetting
{
    #region Fields

    private readonly Setting _Owner;

    #endregion

    #region Lifecycle

    /// <summary>Creates a view over <paramref name="owner"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    public ReadOnlySettingView(Setting owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _Owner = owner;
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    public string Name => _Owner.Name;

    /// <inheritdoc/>
    public string UiName => _Owner.UiName;

    /// <inheritdoc/>
    public string? Description => _Owner.Description;

    /// <inheritdoc/>
    public string GroupName => _Owner.GroupName;

    /// <inheritdoc/>
    public SettingType Type => _Owner.Type;

    /// <inheritdoc/>
    public SettingValue DefaultValue => _Owner.DefaultValue;

    /// <inheritdoc/>
    public SettingValue? MinValue => _Owner.MinValue;

    /// <inheritdoc/>
    public SettingValue? MaxValue => _Owner.MaxValue;

    /// <inheritdoc/>
    public EnumSettingMetadata? EnumMetadata => _Owner.EnumMetadata;

    #endregion

    #region Current Value

    /// <inheritdoc/>
    public SettingValue Value => _Owner.Value;

    /// <inheritdoc/>
    public SettingValue PendingValue => _Owner.PendingValue;

    /// <inheritdoc/>
    public bool IsDirty => _Owner.IsDirty;

    #endregion

    #region TryGetAs*

    /// <inheritdoc/>
    public bool TryGetAsBool(out bool value) => _Owner.TryGetAsBool(out value);

    /// <inheritdoc/>
    public bool TryGetAsString(out string value) => _Owner.TryGetAsString(out value);

    /// <inheritdoc/>
    public bool TryGetAsF64(out double value) => _Owner.TryGetAsF64(out value);

    /// <inheritdoc/>
    public bool TryGetAsU64(out ulong value) => _Owner.TryGetAsU64(out value);

    /// <inheritdoc/>
    public bool TryGetAsI64(out long value) => _Owner.TryGetAsI64(out value);

    /// <inheritdoc/>
    public bool TryGetAsBytes(out byte[] value) => _Owner.TryGetAsBytes(out value);

    /// <inheritdoc/>
    public bool TryGetAsEnum(out (string Name, ulong Value) value) => _Owner.TryGetAsEnum(out value);

    /// <inheritdoc/>
    public bool TryGetAsBoolArray(out bool[] value) => _Owner.TryGetAsBoolArray(out value);

    /// <inheritdoc/>
    public bool TryGetAsStringArray(out string[] value) => _Owner.TryGetAsStringArray(out value);

    /// <inheritdoc/>
    public bool TryGetAsF64Array(out double[] value) => _Owner.TryGetAsF64Array(out value);

    /// <inheritdoc/>
    public bool TryGetAsU64Array(out ulong[] value) => _Owner.TryGetAsU64Array(out value);

    /// <inheritdoc/>
    public bool TryGetAsI64Array(out long[] value) => _Owner.TryGetAsI64Array(out value);

    #endregion

    #region Internal helpers

    /// <summary>
    /// Wraps a snapshot of mutable settings as read-only struct views.
    /// Allocates one array of views; does not copy setting state.
    /// </summary>
    internal static IReadOnlyList<ReadOnlySettingView> Wrap(IReadOnlyList<Setting> settings)
    {
        int count = settings.Count;
        if (count == 0)
        {
            return [];
        }

        ReadOnlySettingView[] views = new ReadOnlySettingView[count];
        for (int i = 0; i < count; i++)
        {
            views[i] = new ReadOnlySettingView(settings[i]);
        }

        return views;
    }

    #endregion
}
