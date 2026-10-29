// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// A byte writer for hand-built SOME/IP messages and payloads. Every multi-byte method writes big-endian, which is the byte order of
/// SOME/IP unless a configuration says otherwise; the little-endian variants exist for the types that ask for it.
/// <para>
/// Why a writer of its own and not the frame builder: the frame builder emits well-formed messages, and the tests have to
/// produce the malformed ones as well (a length that contradicts the data, a wrong version, a header cut short). Writing the bytes
/// by hand also keeps each test readable as the wire layout it checks. <b>Thread-safety:</b> not thread-safe, one instance per test.
/// </para>
/// </summary>
internal sealed class SomeIpBytes
{
    private readonly List<byte> _Bytes = [];

    /// <summary>The number of bytes written so far.</summary>
    internal int Length => _Bytes.Count;

    /// <summary>Writes one byte.</summary>
    internal SomeIpBytes U8(byte value)
    {
        _Bytes.Add(value);
        return this;
    }

    /// <summary>Writes a big-endian 16-bit unsigned integer.</summary>
    internal SomeIpBytes U16(ushort value) => U8((byte)(value >> 8)).U8((byte)value);

    /// <summary>Writes a big-endian 32-bit unsigned integer.</summary>
    internal SomeIpBytes U32(uint value) => U16((ushort)(value >> 16)).U16((ushort)value);

    /// <summary>Writes a big-endian 64-bit unsigned integer.</summary>
    internal SomeIpBytes U64(ulong value) => U32((uint)(value >> 32)).U32((uint)value);

    /// <summary>Writes a signed byte.</summary>
    internal SomeIpBytes I8(sbyte value) => U8((byte)value);

    /// <summary>Writes a big-endian 16-bit signed integer.</summary>
    internal SomeIpBytes I16(short value) => U16((ushort)value);

    /// <summary>Writes a big-endian 32-bit signed integer.</summary>
    internal SomeIpBytes I32(int value) => U32((uint)value);

    /// <summary>Writes a big-endian 64-bit signed integer.</summary>
    internal SomeIpBytes I64(long value) => U64((ulong)value);

    /// <summary>Writes a big-endian 32-bit float.</summary>
    internal SomeIpBytes F32(float value) => U32(BitConverter.SingleToUInt32Bits(value));

    /// <summary>Writes a big-endian 64-bit float.</summary>
    internal SomeIpBytes F64(double value) => U64(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Writes a little-endian 16-bit unsigned integer.</summary>
    internal SomeIpBytes U16Le(ushort value) => U8((byte)value).U8((byte)(value >> 8));

    /// <summary>Writes a little-endian 32-bit unsigned integer.</summary>
    internal SomeIpBytes U32Le(uint value) => U16Le((ushort)value).U16Le((ushort)(value >> 16));

    /// <summary>Writes raw bytes.</summary>
    internal SomeIpBytes Bytes(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value)
        {
            _Bytes.Add(b);
        }

        return this;
    }

    /// <summary>Writes <paramref name="count"/> bytes of the given value.</summary>
    internal SomeIpBytes Fill(byte value, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _Bytes.Add(value);
        }

        return this;
    }

    /// <summary>Writes a string as UTF-8 without a terminator.</summary>
    internal SomeIpBytes Utf8(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    /// <summary>Writes a string as UTF-16 big-endian without a terminator.</summary>
    internal SomeIpBytes Utf16(string value) => Bytes(Encoding.BigEndianUnicode.GetBytes(value));

    /// <summary>The bytes written so far.</summary>
    internal byte[] ToArray() => [.. _Bytes];

    /// <summary>
    /// Builds one complete SOME/IP message: the 16-byte header and the payload. The length field is <c>8 + payload length</c>
    /// unless <paramref name="lengthOverride"/> is given, which lets a test write the header that contradicts its data.
    /// </summary>
    /// <param name="serviceId">Service id.</param>
    /// <param name="methodId">Method id.</param>
    /// <param name="payload">The bytes behind the header.</param>
    /// <param name="clientId">Client id.</param>
    /// <param name="sessionId">Session id.</param>
    /// <param name="messageType">Message type byte.</param>
    /// <param name="returnCode">Return code byte.</param>
    /// <param name="interfaceVersion">Interface version.</param>
    /// <param name="protocolVersion">Protocol version.</param>
    /// <param name="lengthOverride">The length field to write instead of the correct one.</param>
    internal static byte[] Message(
        ushort serviceId,
        ushort methodId,
        ReadOnlySpan<byte> payload = default,
        ushort clientId = 0x0001,
        ushort sessionId = 0x0010,
        byte messageType = 0x00,
        byte returnCode = 0x00,
        byte interfaceVersion = 1,
        byte protocolVersion = 1,
        uint? lengthOverride = null)
    {
        return new SomeIpBytes()
            .U16(serviceId)
            .U16(methodId)
            .U32(lengthOverride ?? (uint)(8 + payload.Length))
            .U16(clientId)
            .U16(sessionId)
            .U8(protocolVersion)
            .U8(interfaceVersion)
            .U8(messageType)
            .U8(returnCode)
            .Bytes(payload)
            .ToArray();
    }

    /// <summary>Concatenates byte arrays, for a datagram that holds several messages.</summary>
    /// <param name="parts">The parts in order.</param>
    internal static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(static p => p)];
}
