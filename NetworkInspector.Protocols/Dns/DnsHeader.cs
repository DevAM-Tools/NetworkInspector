// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Dns;

/// <summary>
/// DNS header (12 bytes, RFC 1035 Section 4.1.1).
/// <code>
///  0  1  2  3  4  5  6  7  8  9  10 11 12 13 14 15
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                      ID                         |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |QR| Opcode  |AA|TC|RD|RA| Z|AD|CD|   RCODE      |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    QDCOUNT                       |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    ANCOUNT                       |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    NSCOUNT                       |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    ARCOUNT                       |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// </code>
/// </summary>
/// <param name="TransactionId">Transaction ID.</param>
/// <param name="Flags">Raw flags word (2 bytes).</param>
/// <param name="QuestionCount">Number of questions.</param>
/// <param name="AnswerCount">Number of answer resource records.</param>
/// <param name="AuthorityCount">Number of authority resource records.</param>
/// <param name="AdditionalCount">Number of additional resource records.</param>
internal readonly record struct DnsHeader(
    ushort TransactionId,
    ushort Flags,
    ushort QuestionCount,
    ushort AnswerCount,
    ushort AuthorityCount,
    ushort AdditionalCount)
{
    #region Constants

    /// <summary>Minimum header size in bytes.</summary>
    internal const int Size = 12;

    #endregion

    #region Flag Accessors

    // Flag bit positions within the 16-bit flags word
    // Bit 15: QR (1 = response, 0 = query)
    // Bits 14-11: Opcode
    // Bit 10: AA (Authoritative Answer)
    // Bit 9: TC (Truncation)
    // Bit 8: RD (Recursion Desired)
    // Bit 7: RA (Recursion Available)
    // Bit 6: Z (Reserved)
    // Bit 5: AD (Authenticated Data, RFC 4035)
    // Bit 4: CD (Checking Disabled, RFC 4035)
    // Bits 3-0: RCODE

    /// <summary>True if this is a response (QR=1).</summary>
    internal bool IsResponse => (Flags & 0x8000) != 0;

    /// <summary>Operation code (4 bits).</summary>
    internal byte Opcode => (byte)((Flags >> 11) & 0x0F);

    /// <summary>Authoritative answer flag.</summary>
    internal bool IsAuthoritative => (Flags & 0x0400) != 0;

    /// <summary>Truncation flag.</summary>
    internal bool IsTruncated => (Flags & 0x0200) != 0;

    /// <summary>Recursion desired flag.</summary>
    internal bool RecursionDesired => (Flags & 0x0100) != 0;

    /// <summary>Recursion available flag.</summary>
    internal bool RecursionAvailable => (Flags & 0x0080) != 0;

    /// <summary>Z (reserved) bit.</summary>
    internal bool Z => (Flags & 0x0040) != 0;

    /// <summary>Authenticated data flag (RFC 4035).</summary>
    internal bool AuthenticatedData => (Flags & 0x0020) != 0;

    /// <summary>Checking disabled flag (RFC 4035).</summary>
    internal bool CheckingDisabled => (Flags & 0x0010) != 0;

    /// <summary>Response code (4 bits).</summary>
    internal byte ResponseCode => (byte)(Flags & 0x000F);

    #endregion

    #region Parsing

    /// <summary>
    /// Tries to parse a DNS header from the given span.
    /// Returns false if the data is shorter than <see cref="Size"/> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryParse(ReadOnlySpan<byte> data, out DnsHeader header)
    {
        if (data.Length < Size)
        {
            header = default;
            return false;
        }

        header = new DnsHeader(
            BinaryPrimitives.ReadUInt16BigEndian(data),
            BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(data[10..]));
        return true;
    }

    #endregion
}
