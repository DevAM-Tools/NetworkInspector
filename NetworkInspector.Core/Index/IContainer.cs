// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Index;

/// <summary>
/// Common interface for Roaring Bitmap containers.
/// Each container covers a 16-bit range (up to 65536 values).
/// </summary>
internal interface IContainer
{
    #region Methods

    /// <summary>Adds a value to the container.</summary>
    IContainer Add(ushort value);

    /// <summary>Checks if a value is present.</summary>
    bool Contains(ushort value);

    /// <summary>Number of values stored.</summary>
    int Cardinality
    {
        get;
    }

    /// <summary>AND (intersection) with another container.</summary>
    IContainer And(IContainer other);

    /// <summary>OR (union) with another container.</summary>
    IContainer Or(IContainer other);

    /// <summary>ANDNOT (difference): this AND NOT other.</summary>
    IContainer AndNot(IContainer other);

    /// <summary>XOR (symmetric difference) with another container.</summary>
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
