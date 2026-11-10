// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// The values a front end chose for the limits of <see cref="SettingsLimits"/>: the values the manager starts with. Each starts at
/// the default of its definition, so <c>new SettingsLimitValues()</c> is the secure default and
/// <c>with { MaxFileSize = … }</c> changes one value. Immutable.
/// </summary>
/// <remarks>
/// One object instead of one parameter per limit: a swapped argument cannot compile, and a new limit does not change a
/// signature. The values are checked by <see cref="SettingsManager"/> when it is created (<see cref="Validate"/>), not here,
/// because a record cannot refuse an <c>init</c> value without a constructor of its own. <c>default(LimitValue)</c> is
/// <c>Of(0)</c>, which is below every range, so a forgotten value is refused and never runs open.
/// <para>Thread safety: immutable, so safe to share.</para>
/// <para>Sync: one member per definition of <see cref="SettingsLimits"/> that the manager reads.</para>
/// </remarks>
/// <seealso cref="SettingsLimits"/>
/// <seealso cref="SettingsManager"/>
public sealed record SettingsLimitValues
{
    /// <summary>The value of <see cref="SettingsLimits.MaxFileSize"/>: the largest file that is read.</summary>
    public LimitValue MaxFileSize { get; init; } = SettingsLimits.MaxFileSize.DefaultValue;

    /// <summary>The value of <see cref="SettingsLimits.MaxJsonDepth"/>: the deepest JSON nesting that is read.</summary>
    public LimitValue MaxJsonDepth { get; init; } = SettingsLimits.MaxJsonDepth.DefaultValue;

    /// <summary>The value of <see cref="SettingsLimits.FolderLockTimeout"/>: the longest wait for the folder lock.</summary>
    public LimitValue FolderLockTimeout { get; init; } = SettingsLimits.FolderLockTimeout.DefaultValue;

    /// <summary>The value of <see cref="SettingsLimits.MaxGroupFiles"/>: the most group files that one load reads.</summary>
    public LimitValue MaxGroupFiles { get; init; } = SettingsLimits.MaxGroupFiles.DefaultValue;

    /// <summary>The value of <see cref="SettingsLimits.MaxTextLength"/>: the longest value text that is parsed.</summary>
    public LimitValue MaxTextLength { get; init; } = SettingsLimits.MaxTextLength.DefaultValue;

    /// <summary>The secure defaults of every limit.</summary>
    public static SettingsLimitValues Default { get; } = new();

    /// <summary>Checks every value against the hard range of its definition.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A value is outside the range of its definition (and not an allowed <c>off</c>); the message holds the key and the range.
    /// </exception>
    public void Validate()
    {
        SettingsLimits.MaxFileSize.ThrowIfInvalid(MaxFileSize, nameof(MaxFileSize));
        SettingsLimits.MaxJsonDepth.ThrowIfInvalid(MaxJsonDepth, nameof(MaxJsonDepth));
        SettingsLimits.FolderLockTimeout.ThrowIfInvalid(FolderLockTimeout, nameof(FolderLockTimeout));
        SettingsLimits.MaxGroupFiles.ThrowIfInvalid(MaxGroupFiles, nameof(MaxGroupFiles));
        SettingsLimits.MaxTextLength.ThrowIfInvalid(MaxTextLength, nameof(MaxTextLength));
    }
}
