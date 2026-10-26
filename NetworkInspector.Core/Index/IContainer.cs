// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Common interface for Roaring Bitmap containers.
/// Each container covers a 16-bit range (up to 65536 values): the owning bitmap picks the container by the high 16 bits of
/// a 32-bit value, and the container stores only the low 16 bits.
/// <para>
/// Set operations (<see cref="And"/>, <see cref="Or"/>, <see cref="AndNot"/>, <see cref="Xor"/>)
/// return a (possibly new) container and must not mutate either operand.
/// <see cref="Add"/> may mutate <see langword="this"/>.
/// </para>
/// <para>
/// <b>Container kinds and conversion:</b> the implementations are <see cref="ArrayContainer"/> (sparse: at most
/// <see cref="ArrayContainer.MaxCapacity"/> sorted values), <see cref="BitmapContainer"/> (dense: a fixed 8 KiB bit set) and
/// the run-length container (dense sequential ranges, created when a completed chunk is smaller as runs). An array becomes a
/// bitmap when an addition or a union would push it past its capacity, and a bitmap result of a set operation becomes an array
/// again when it is sparse. That is why <see cref="Add"/> and the set operations return an <see cref="IContainer"/> that the
/// caller must keep using instead of the instance it called: the returned container may be a different object of a different kind.
/// </para>
/// <para>
/// <b>Thread-safety:</b> containers have no synchronization of their own and are not thread-safe. The owning
/// <see cref="RoaringBitmap"/> supplies the protection (one writer, readers validated by a sequence lock that retries a
/// read overlapping a mutation), so a read racing a write may return a wrong answer or throw and is then discarded and
/// repeated by the owner. Do not call a container directly from several threads.
/// </para>
/// </summary>
internal interface IContainer
{
    #region Methods

    /// <summary>
    /// Adds a value to the container. May mutate <see langword="this"/>. Adding a value that is already present changes nothing.
    /// </summary>
    /// <returns>
    /// The container to use from now on: <see langword="this"/> in the common case, or a new container of another kind when
    /// the addition made the current representation the wrong one (for example an array that outgrew its capacity).
    /// </returns>
    IContainer Add(ushort value);

    /// <summary>Checks if a value is present.</summary>
    bool Contains(ushort value);

    /// <summary>Number of values stored.</summary>
    int Cardinality
    {
        get;
    }

    /// <summary>
    /// AND (intersection) with another container.
    /// Must not mutate <see langword="this"/> or <paramref name="other"/>;
    /// return a new container or a clone when in-place <see cref="Add"/> would alias an operand.
    /// </summary>
    IContainer And(IContainer other);

    /// <summary>
    /// OR (union) with another container.
    /// Must not mutate <see langword="this"/> or <paramref name="other"/>;
    /// return a new container or a clone when in-place <see cref="Add"/> would alias an operand.
    /// </summary>
    IContainer Or(IContainer other);

    /// <summary>
    /// ANDNOT (difference): this AND NOT other.
    /// Must not mutate <see langword="this"/> or <paramref name="other"/>;
    /// return a new container or a clone when in-place <see cref="Add"/> would alias an operand.
    /// </summary>
    IContainer AndNot(IContainer other);

    /// <summary>
    /// XOR (symmetric difference) with another container.
    /// Must not mutate <see langword="this"/> or <paramref name="other"/>;
    /// return a new container or a clone when in-place <see cref="Add"/> would alias an operand.
    /// </summary>
    IContainer Xor(IContainer other);

    /// <summary>Creates a deep copy of this container. The copy is fully independent.</summary>
    IContainer Clone();

    #endregion

    #region Properties

    /// <summary>
    /// The minimum value in the container.
    /// <para><b>Precondition:</b> <see cref="Cardinality"/> must be &gt; 0. Behaviour is undefined on an empty container.</para>
    /// </summary>
    ushort Min
    {
        get;
    }

    /// <summary>
    /// The maximum value in the container.
    /// <para><b>Precondition:</b> <see cref="Cardinality"/> must be &gt; 0. Behaviour is undefined on an empty container.</para>
    /// </summary>
    ushort Max
    {
        get;
    }

    #endregion
}
