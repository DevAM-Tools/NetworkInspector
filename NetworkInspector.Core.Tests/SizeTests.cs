// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests that verify the binary layout sizes of hot-path structs
/// to catch unintentional size regressions.
/// </summary>
internal sealed class SizeTests
{
    /// <summary>
    /// ParseError must stay compact to keep ParseResult return values small.
    /// Three fields: Kind (1 byte) + ProtocolName (8 bytes ref) + Message (8 bytes ref)
    /// = 24 bytes on x64 with StructLayout.Auto.
    /// </summary>
    [Test]
    public async Task ParseError_Size_Must_Stay_Compact()
    {
        int parseErrorSize = Unsafe.SizeOf<ParseError>();

        // 24 bytes: Kind(1) + padding(7) + ProtocolName(8) + Message(8)
        // If this regresses, the error path slows down.
        await Assert.That(parseErrorSize).IsLessThanOrEqualTo(24);
    }

    /// <summary>
    /// Non-generic ParseResult is returned through the entire protocol parse chain.
    /// Must be exactly 4 bytes (single encoded int) for maximum inlining and register passing.
    /// </summary>
    [Test]
    public async Task ParseResult_Size_Must_Be_4_Bytes()
    {
        int parseResultSize = Unsafe.SizeOf<ParseResult>();

        // ParseResult: single int (_EncodedValue) = 4 bytes
        await Assert.That(parseResultSize).IsEqualTo(4);
    }

    /// <summary>
    /// Field caches FieldId in its padding bytes. Must stay at 16 bytes (Packet ref + ushort index + FieldId).
    /// Growing beyond 16 bytes would mean the FieldId cache occupies extra space rather than free padding.
    /// </summary>
    [Test]
    public async Task Field_Size_Must_Stay_At_16_Bytes()
    {
        int fieldSize = Unsafe.SizeOf<Field>();

        // Layout: _Packet(8) + _Index(2) + alignment-pad(2) + _FieldId(4) = 16
        await Assert.That(fieldSize).IsEqualTo(16);
    }

    /// <summary>
    /// FieldValueData is stored in every field of every packet. Must stay at 24 bytes.
    /// Layout: _Data(8) + _Data1(8) + _Ref(8) = 24 bytes with Auto layout.
    /// _Data1 was added for 128-bit inline storage of IPv6Address and Uuid types,
    /// avoiding boxing. FieldType discriminant is encoded into _Ref via marker singletons.
    /// </summary>
    [Test]
    public async Task FieldValueData_Size_Must_Stay_At_24_Bytes()
    {
        int fieldValueDataSize = Unsafe.SizeOf<FieldValueData>();

        await Assert.That(fieldValueDataSize).IsEqualTo(24);
    }

    /// <summary>
    /// FieldValue wraps FieldValueData + an optional LazyString for custom display text.
    /// Must stay at 32 bytes: FieldValueData(24) + LazyString(8).
    /// </summary>
    [Test]
    public async Task FieldValue_Size_Must_Stay_At_32_Bytes()
    {
        int fieldValueSize = Unsafe.SizeOf<FieldValue>();

        await Assert.That(fieldValueSize).IsEqualTo(32);
    }
}