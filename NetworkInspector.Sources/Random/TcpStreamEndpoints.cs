// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using NetworkInspector.Core;

namespace NetworkInspector.Sources.Random;

/// <summary>
/// Deterministically derived network endpoints for a single TCP connection.
/// All fields are derived from <c>DeriveFrameSeed(masterSeed, streamIndex)</c>
/// so the same (masterSeed, streamIndex) pair always produces identical endpoints.
/// </summary>
internal readonly struct TcpStreamEndpoints
{
    #region Properties

    internal byte[] ClientMac
    {
        get;
    }
    internal byte[] ServerMac
    {
        get;
    }
    internal byte[] ClientIp
    {
        get;
    }
    internal byte[] ServerIp
    {
        get;
    }
    internal ushort ClientPort
    {
        get;
    }
    internal ushort ServerPort
    {
        get;
    }
    internal uint ClientIsn
    {
        get;
    }
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
        // Derive a per-connection seed. Use a high offset to avoid collisions
        // with per-frame seeds that use small indices.
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

        // Ensure unicast MACs (clear multicast bit, set locally-administered bit)
        ClientMac[0] = (byte)((ClientMac[0] & 0xFE) | 0x02);
        ServerMac[0] = (byte)((ServerMac[0] & 0xFE) | 0x02);

        // Ephemeral port range 1024–65534
        ClientPort = (ushort)((rng.NextU64() % 64511) + 1024);
        ServerPort = (ushort)((rng.NextU64() % 64511) + 1024);

        ClientIsn = rng.NextU32();
        ServerIsn = rng.NextU32();
    }

    #endregion
}
