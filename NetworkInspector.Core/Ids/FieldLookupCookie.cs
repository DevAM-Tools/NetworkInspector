// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Opaque continuation token for iterating over multiple occurrences of a field
/// in a <see cref="Packet"/>'s flat field array via
/// <see cref="Packet.TryGetNextFieldValue"/>.
/// <para>
/// Callers must not inspect or modify the internal state. Use <see cref="Start"/>
/// to obtain an initial cookie, then pass it by reference to successive calls.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FieldLookupCookie
{
    #region Properties

    /// <summary>Returns a cookie positioned at the beginning of the field array.</summary>
    public static FieldLookupCookie Start => default;

    #endregion

    #region Fields

    /// <summary>
    /// Internal position in the flat field array. Only <see cref="Packet"/> reads/writes this.
    /// Zero-initialized by <see cref="Start"/> (scans from the first field).
    /// </summary>
    internal int Position;

    #endregion
}
