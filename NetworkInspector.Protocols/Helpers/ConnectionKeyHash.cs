// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Helpers;

/// <summary>
/// Shared helper of the UDP and TCP connection keys: feeds 128-bit addresses into a process-seeded
/// <see cref="HashCode"/> as four 32-bit words.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UInt128.GetHashCode"/> and <see cref="ulong.GetHashCode"/> fold their bits before the seed is applied,
/// so distinct addresses with equal folds would collide whatever the seed. Adding 32-bit words keeps the input
/// injective; only the seeded mix decides the bucket.
/// </para>
/// <para><b>Thread-safety:</b> stateless.</para>
/// </remarks>
internal static class ConnectionKeyHash
{
    #region Internal API

    /// <summary>Adds the four 32-bit words of <paramref name="address"/>, low word first.</summary>
    /// <param name="hash">The hash being built.</param>
    /// <param name="address">A 128-bit address (IPv4 addresses are IPv4-mapped).</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AddAddress(ref HashCode hash, UInt128 address)
    {
        ulong low = (ulong)address;
        ulong high = (ulong)(address >> 64);
        hash.Add((uint)low);
        hash.Add((uint)(low >> 32));
        hash.Add((uint)high);
        hash.Add((uint)(high >> 32));
    }

    #endregion
}
