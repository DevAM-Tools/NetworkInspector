// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Ids;

/// <summary>
/// Opaque continuation token for iterating over multiple occurrences of a field
/// in a <see cref="Packet"/>'s flat field array via
/// <see cref="Packet.TryGetNextField"/>.
/// <para>
/// Callers must not inspect or modify the internal state. Use <see cref="Start"/>
/// to obtain an initial cookie, then pass it by reference to successive calls.
/// </para>
/// <para>
/// <b>Design:</b> the cookie is only the index at which the next scan of the packet's flat field array resumes, so a lookup
/// of repeated fields (for example the outer and inner IP header of a tunnel) costs no allocation and no per-call state on the
/// packet. It is a mutable struct on purpose: the packet advances it in place through the <c>ref</c> argument. After a
/// successful call it points just behind the found field; after an unsuccessful call it is parked at the end of the searched
/// range, so another call with the same cookie finds nothing new unless lazy materialization appended fields.
/// </para>
/// <para>
/// <b>Misuse:</b> a cookie belongs to one packet and one field id. Reusing it with another packet or another field id has no
/// defined meaning (it would skip fields before its position). <c>default(FieldLookupCookie)</c> equals <see cref="Start"/>.
/// </para>
/// <para><b>Thread-safety:</b> not thread-safe; use one cookie per lookup loop and thread.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FieldLookupCookie
{
    /// <summary>Returns a cookie positioned at the beginning of the field array.</summary>
    public static FieldLookupCookie Start => default;

    /// <summary>
    /// Internal position in the flat field array. Only <see cref="Packet"/> reads/writes this.
    /// Zero-initialized by <see cref="Start"/> (scans from the first field).
    /// </summary>
    internal int Position;
}
