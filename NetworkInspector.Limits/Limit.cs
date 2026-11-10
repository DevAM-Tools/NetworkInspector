// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>
/// The read side of the live value of one limit for one consumer: the object that hot code reads while a store (a settings
/// manager) changes the value through its <see cref="LimitWriter"/>. Thread-safe: any thread may read; reads are lock-free and
/// allocation-free. This type has no way to change the value, so a component that receives it cannot switch a protection off.
/// </summary>
/// <remarks>
/// <see cref="LimitDefinition"/> is the declaration (static, shared, immutable), <see cref="LimitValue"/> is one value, and
/// this class is the cell between them. Create one per consumer, not per definition: two stores that share a
/// definition must not share a holder. Enforcing code keeps the object and reads <see cref="Ceiling"/> once per decision:
/// <c>if (size &gt; limit.Ceiling) { refuse }</c>.
/// <para>Perf: one aligned 8-byte word, published with <c>Volatile.Write</c> and read with <c>Volatile.Read</c>. No lock,
/// no allocation, no lookup per read. Concurrent writers: the last write wins;
/// every published value is valid, so a reader never sees an invalid or torn one. Rejected: a lookup in the settings store per
/// read (a dictionary access per item) and a lock (a kernel wait on the hot path).</para>
/// <para>Invariant: the word holds an encoding of <see cref="LimitValue"/> (-1 for off, otherwise a value of the range); a
/// read-only limit never leaves the default.</para>
/// <para>Security: the type is created only by <see cref="LimitWriter"/>, which holds the one way to change it. Rejected: a public
/// <c>TrySet</c> on this type, because every consumer that may read a limit would then be able to change it.</para>
/// <para>Sync: the encoding is <see cref="LimitValue.Bits"/> and <see cref="LimitValue.FromBits(long)"/>.</para>
/// </remarks>
/// <seealso cref="LimitDefinition"/>
/// <seealso cref="LimitValue"/>
/// <seealso cref="LimitWriter"/>
public sealed class Limit
{
    #region Live word

    /// <summary>The encoded current value; see the remarks of the type for the access rules.</summary>
    private long _Bits;

    /// <summary>Creates a holder that starts at the default of <paramref name="definition"/>.</summary>
    /// <param name="definition">The declaration; its range and flags rule every later change. Not null: the only caller is
    /// <see cref="LimitWriter"/>, which checks it.</param>
    internal Limit(LimitDefinition definition)
    {
        Definition = definition;
        _Bits = definition.DefaultValue.Bits;
    }

    /// <summary>The declaration this holder follows.</summary>
    public LimitDefinition Definition { get; }

    /// <summary>The encoded current value, for the comparison of the writer.</summary>
    internal long Bits => Volatile.Read(ref _Bits);

    /// <summary>Publishes a validated value; only <see cref="LimitWriter"/> calls it.</summary>
    /// <param name="bits">An encoding of a valid value (<see cref="LimitValue.Bits"/>).</param>
    internal void Publish(long bits) => Volatile.Write(ref _Bits, bits);

    #endregion

    #region Reading the value

    /// <summary>
    /// The value to compare against: <see cref="long.MaxValue"/> when the limit is off, so <c>input &gt; Ceiling</c> is never
    /// true, otherwise the number in the base unit. One volatile read; read it once per decision into a local.
    /// </summary>
    public long Ceiling
    {
        get
        {
            long bits = Volatile.Read(ref _Bits);
            return bits < 0
                ? long.MaxValue
                : bits;
        }
    }

    /// <summary>Whether the limit is switched off.</summary>
    public bool IsOff => Volatile.Read(ref _Bits) < 0;

    /// <summary>The current value, for display and the hit text; for a comparison read <see cref="Ceiling"/>.</summary>
    public LimitValue Value => LimitValue.FromBits(Volatile.Read(ref _Bits));

    #endregion
}
