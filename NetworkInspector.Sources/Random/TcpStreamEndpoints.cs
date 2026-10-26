// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Deterministically derived network endpoints for a single TCP connection.
/// All fields are derived from <c>DeriveFrameSeed(masterSeed, streamIndex)</c>
/// so the same (masterSeed, streamIndex) pair always produces identical endpoints.
/// </summary>
/// <remarks>
/// Stateless by design: nothing is cached between frames, so a frame can be generated from its index alone (random
/// access, <c>FrameById</c>) at the price of deriving the endpoints again for every frame (four small arrays). This is
/// a synthetic test source, not a hot path. A <c>readonly struct</c> does not make its arrays immutable: they are
/// handed to the frame assembly and must be treated as read-only. Thread-safe once constructed (never mutated).
/// </remarks>
internal readonly struct TcpStreamEndpoints
{
    #region Properties

    /// <summary>Client MAC address (6 bytes, unicast, locally administered).</summary>
    internal byte[] ClientMac
    {
        get;
    }

    /// <summary>Server MAC address (6 bytes, unicast, locally administered).</summary>
    internal byte[] ServerMac
    {
        get;
    }

    /// <summary>Client IP address: 4 bytes (IPv4) or 16 bytes (IPv6), random.</summary>
    internal byte[] ClientIp
    {
        get;
    }

    /// <summary>Server IP address: 4 bytes (IPv4) or 16 bytes (IPv6), random.</summary>
    internal byte[] ServerIp
    {
        get;
    }

    /// <summary>Client TCP port, 1024 to 65534.</summary>
    internal ushort ClientPort
    {
        get;
    }

    /// <summary>Server TCP port, 1024 to 65534.</summary>
    internal ushort ServerPort
    {
        get;
    }

    /// <summary>Initial sequence number of the client (random).</summary>
    internal uint ClientIsn
    {
        get;
    }

    /// <summary>Initial sequence number of the server (random).</summary>
    internal uint ServerIsn
    {
        get;
    }

    #endregion

    #region Constructors

    /// <summary>
    /// Derives deterministic endpoints for connection <paramref name="streamIndex"/>.
    /// Uses a seed namespace offset to avoid collisions with frame-level seeds.
    /// </summary>
    /// <param name="masterSeed">Master PRNG seed from the source.</param>
    /// <param name="streamIndex">Zero-based connection index.</param>
    /// <param name="isIpv6">Whether to generate 16-byte IPv6 addresses instead of 4-byte IPv4.</param>
    internal TcpStreamEndpoints(ulong masterSeed, int streamIndex, bool isIpv6)
    {
        // Derive a per-connection seed. DeriveFrameSeed mixes (masterSeed XOR id), and the random frame modes use ids below
        // 2^32 (a uint frame index), so the offset 2^32 moves the connection ids out of that range and no connection
        // seed equals a frame seed of the same master seed. The payload seeds of TcpStreamFrameBuilder use the next
        // offsets (2 x 2^32 and 3 x 2^32) for the same reason.
        ulong connectionSeed = Xoroshiro128PlusPlus.DeriveFrameSeed(
            masterSeed, (ulong)streamIndex + 0x1_0000_0000UL);
        Xoroshiro128PlusPlus rng = new(connectionSeed);

        int ipSize = isIpv6 ? 16 : 4;
        ClientIp = new byte[ipSize];
        ServerIp = new byte[ipSize];
        ClientMac = new byte[6];
        ServerMac = new byte[6];

        rng.FillBytes(ClientMac);
        rng.FillBytes(ServerMac);
        rng.FillBytes(ClientIp);
        rng.FillBytes(ServerIp);

        // Ensure unicast MACs (clear the group/multicast bit 0 of the first octet, set the locally-administered bit 1) so
        // the random addresses can never collide with a real vendor address or be read as multicast/broadcast.
        ClientMac[0] = (byte)((ClientMac[0] & 0xFE) | 0x02);
        ServerMac[0] = (byte)((ServerMac[0] & 0xFE) | 0x02);

        // Non-privileged port range 1024–65534: 64 511 values starting at 1024. The modulo bias is irrelevant for
        // synthetic data. The random IP addresses are not constrained: a stream may use any address, including
        // multicast or reserved ranges.
        ClientPort = (ushort)((rng.NextU64() % 64511) + 1024);
        ServerPort = (ushort)((rng.NextU64() % 64511) + 1024);

        ClientIsn = rng.NextU32();
        ServerIsn = rng.NextU32();
    }

    #endregion
}
