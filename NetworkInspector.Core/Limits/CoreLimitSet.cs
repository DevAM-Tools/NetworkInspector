// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Limits;

/// <summary>
/// The live holders of every limit of <see cref="CoreLimits"/> for one stack: what an owner reads once per decision
/// (<see cref="Limit.Ceiling"/>, one volatile read, no allocation). The holders follow the settings of the stack; a change of a
/// setting is visible to the next read.
/// </summary>
/// <remarks>
/// Obtained from <see cref="Stack.Limits"/>. <see cref="Defaults"/> serves owners that exist without a stack (a registry or a
/// reassembler built by hand): its holders carry the fixed defaults and nothing can change them. Thread-safe: the holders are
/// immutable references and each read is a volatile read.
/// </remarks>
/// <seealso cref="CoreLimits"/>
public sealed class CoreLimitSet
{
    /// <summary>
    /// The fixed defaults, shared by every owner that has no stack. The writers that created the holders are not kept, so no
    /// code can change the values.
    /// </summary>
    public static CoreLimitSet Defaults { get; } = new(null);

    /// <summary>Live holder of <see cref="CoreLimits.MaxStreamBufferSize"/>.</summary>
    public Limit MaxStreamBufferSize { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxStreamPduSize"/>.</summary>
    public Limit MaxStreamPduSize { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxStreamRebases"/>.</summary>
    public Limit MaxStreamRebases { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxPendingDatagrams"/>.</summary>
    public Limit MaxPendingDatagrams { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxFragmentStorage"/>.</summary>
    public Limit MaxFragmentStorage { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxFragmentsPerDatagram"/>.</summary>
    public Limit MaxFragmentsPerDatagram { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxLazyDepth"/>.</summary>
    public Limit MaxLazyDepth { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxValueCacheRows"/>.</summary>
    public Limit MaxValueCacheRows { get; }

    /// <summary>Live holder of <see cref="CoreLimits.MaxInterfaces"/>.</summary>
    public Limit MaxInterfaces { get; }

    /// <summary>
    /// Creates the set from the settings of a manager. A limit whose setting is not registered (a manager that never saw the
    /// limit) falls back to the fixed default.
    /// </summary>
    /// <param name="settings">The settings of the stack, or <see langword="null"/> for the fixed defaults.</param>
    private CoreLimitSet(ReadOnlySettingsManagerView? settings)
    {
        MaxStreamBufferSize = _Holder(settings, CoreLimits.MaxStreamBufferSize);
        MaxStreamPduSize = _Holder(settings, CoreLimits.MaxStreamPduSize);
        MaxStreamRebases = _Holder(settings, CoreLimits.MaxStreamRebases);
        MaxPendingDatagrams = _Holder(settings, CoreLimits.MaxPendingDatagrams);
        MaxFragmentStorage = _Holder(settings, CoreLimits.MaxFragmentStorage);
        MaxFragmentsPerDatagram = _Holder(settings, CoreLimits.MaxFragmentsPerDatagram);
        MaxLazyDepth = _Holder(settings, CoreLimits.MaxLazyDepth);
        MaxValueCacheRows = _Holder(settings, CoreLimits.MaxValueCacheRows);
        MaxInterfaces = _Holder(settings, CoreLimits.MaxInterfaces);
    }

    /// <summary>Creates the set that follows the settings of a stack.</summary>
    /// <param name="settings">The read-only view of the settings manager that registered <see cref="CoreLimits.All"/>.</param>
    /// <returns>A set whose holders follow <paramref name="settings"/>.</returns>
    internal static CoreLimitSet FromSettings(ReadOnlySettingsManagerView settings)
    {
        return new CoreLimitSet(settings);
    }

    /// <summary>Converts the ceiling of a limit to an <see cref="int"/> for an owner that stores its bound in an int.</summary>
    /// <param name="limit">The live holder.</param>
    /// <returns>The current ceiling, at most <see cref="int.MaxValue"/> (<c>off</c> reads as the largest value).</returns>
    public static int IntCeiling(Limit limit)
    {
        return (int)Math.Min(limit.Ceiling, int.MaxValue);
    }

    /// <summary>Picks the live holder of a registered setting, or a fixed-default holder.</summary>
    /// <param name="settings">The settings view, or <see langword="null"/>.</param>
    /// <param name="definition">The limit.</param>
    /// <returns>The live holder of the setting when it exists; otherwise a holder at the default that nothing can change.</returns>
    private static Limit _Holder(ReadOnlySettingsManagerView? settings, LimitDefinition definition)
    {
        Limit? live = settings?.GetLiveLimit(definition.Key);
        if (live is not null)
        {
            return live;
        }

        return new LimitWriter(definition).Limit;
    }
}
