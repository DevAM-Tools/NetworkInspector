// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="EndianReader"/>: span reads and swap methods
/// for both big-endian and little-endian byte orders.
/// </summary>
internal sealed class EndianReaderTests
{
    // === Big-endian reads ===

    [Test]
    public async Task ReadU16_BigEndian()
    {
        // byteSwapped=true on LE machine → BigEndian=true
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0x01, 0x02];
        ushort result = reader.ReadU16(data);
        await Assert.That(result).IsEqualTo((ushort)0x0102);
    }

    [Test]
    public async Task ReadU32_BigEndian()
    {
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0x01, 0x02, 0x03, 0x04];
        uint result = reader.ReadU32(data);
        await Assert.That(result).IsEqualTo(0x01020304u);
    }

    [Test]
    public async Task ReadU64_BigEndian()
    {
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        ulong result = reader.ReadU64(data);
        await Assert.That(result).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task ReadI16_BigEndian()
    {
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0xFF, 0xFE]; // -2 in big-endian
        short result = reader.ReadI16(data);
        await Assert.That(result).IsEqualTo((short)-2);
    }

    [Test]
    public async Task ReadI32_BigEndian()
    {
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFE]; // -2 in big-endian
        int result = reader.ReadI32(data);
        await Assert.That(result).IsEqualTo(-2);
    }

    [Test]
    public async Task ReadI64_BigEndian()
    {
        EndianReader reader = new(byteSwapped: true);
        byte[] data = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE]; // -2 in big-endian
        long result = reader.ReadI64(data);
        await Assert.That(result).IsEqualTo(-2L);
    }

    // === Little-endian reads (native on x86/ARM) ===

    [Test]
    public async Task ReadU16_LittleEndian()
    {
        // byteSwapped=false → reads native (LE on LE machine)
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0x02, 0x01];
        ushort result = reader.ReadU16(data);
        await Assert.That(result).IsEqualTo((ushort)0x0102);
    }

    [Test]
    public async Task ReadU32_LittleEndian()
    {
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0x04, 0x03, 0x02, 0x01];
        uint result = reader.ReadU32(data);
        await Assert.That(result).IsEqualTo(0x01020304u);
    }

    [Test]
    public async Task ReadU64_LittleEndian()
    {
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01];
        ulong result = reader.ReadU64(data);
        await Assert.That(result).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task ReadI16_LittleEndian()
    {
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0xFE, 0xFF]; // -2 in little-endian
        short result = reader.ReadI16(data);
        await Assert.That(result).IsEqualTo((short)-2);
    }

    [Test]
    public async Task ReadI32_LittleEndian()
    {
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0xFE, 0xFF, 0xFF, 0xFF]; // -2 in little-endian
        int result = reader.ReadI32(data);
        await Assert.That(result).IsEqualTo(-2);
    }

    [Test]
    public async Task ReadI64_LittleEndian()
    {
        EndianReader reader = new(byteSwapped: false);
        byte[] data = [0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]; // -2 in little-endian
        long result = reader.ReadI64(data);
        await Assert.That(result).IsEqualTo(-2L);
    }

    // === Swap methods ===

    [Test]
    public async Task Swap_U16_NeedSwap()
    {
        EndianReader reader = new(byteSwapped: true);
        ushort swapped = reader.Swap((ushort)0x0102);
        await Assert.That(swapped).IsEqualTo((ushort)0x0201);
    }

    [Test]
    public async Task Swap_U16_NoSwap()
    {
        EndianReader reader = new(byteSwapped: false);
        ushort result = reader.Swap((ushort)0x0102);
        await Assert.That(result).IsEqualTo((ushort)0x0102);
    }

    [Test]
    public async Task Swap_U32_NeedSwap()
    {
        EndianReader reader = new(byteSwapped: true);
        uint swapped = reader.Swap(0x01020304u);
        await Assert.That(swapped).IsEqualTo(0x04030201u);
    }

    [Test]
    public async Task Swap_U32_NoSwap()
    {
        EndianReader reader = new(byteSwapped: false);
        uint result = reader.Swap(0x01020304u);
        await Assert.That(result).IsEqualTo(0x01020304u);
    }

    [Test]
    public async Task Swap_U64_NeedSwap()
    {
        EndianReader reader = new(byteSwapped: true);
        ulong swapped = reader.Swap(0x0102030405060708UL);
        await Assert.That(swapped).IsEqualTo(0x0807060504030201UL);
    }

    [Test]
    public async Task Swap_U64_NoSwap()
    {
        EndianReader reader = new(byteSwapped: false);
        ulong result = reader.Swap(0x0102030405060708UL);
        await Assert.That(result).IsEqualTo(0x0102030405060708UL);
    }

    [Test]
    public async Task Swap_I64_NeedSwap()
    {
        EndianReader reader = new(byteSwapped: true);
        long swapped = reader.Swap(0x0102030405060708L);
        await Assert.That(swapped).IsEqualTo(0x0807060504030201L);
    }

    [Test]
    public async Task Swap_I64_NoSwap()
    {
        EndianReader reader = new(byteSwapped: false);
        long result = reader.Swap(0x0102030405060708L);
        await Assert.That(result).IsEqualTo(0x0102030405060708L);
    }
}
