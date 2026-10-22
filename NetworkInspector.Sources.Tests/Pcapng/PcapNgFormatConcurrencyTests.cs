// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Pcapng;

/// <summary>
/// Sections and their interfaces are single-writer / multi-reader: readers resolve the section of a block offset and
/// the interfaces of a section while the scanner still adds sections.
/// </summary>
internal sealed class PcapNgFormatConcurrencyTests
{
    #region Constants

    private const int _SectionCount = 40;

    #endregion

    #region Fields

    private static readonly LinkType[] _LinkTypes = [LinkType.Ethernet, LinkType.CanSocketcan, LinkType.Lin];

    #endregion

    #region Tests

    [Test]
    public async Task SectionLookupDuringALazyScanReturnsTheSectionOfTheOffset()
    {
        // Arrange: 40 sections of alternating byte order, three IDBs and two packets each.
        List<byte> file = [];
        long[] starts = new long[_SectionCount];
        for (int s = 0; s < _SectionCount; s++)
        {
            starts[s] = file.Count;
            file.AddRange(_Section(bigEndian: s % 2 == 1, firstPayload: s));
        }

        byte[] data = [.. file];
        using DataBackend backend = DataBackend.FromMemory(data);
        IncrementalScanner scanner = new(backend, backend.FileSize);
        PcapNgFormat format = (PcapNgFormat)scanner.Format;
        int done = 0;
        int failures = 0;
        int lookups = 0;

        // Act: two readers resolve offsets inside every published section and read its published interfaces.
        Thread[] readers = new Thread[2];
        for (int r = 0; r < readers.Length; r++)
        {
            int seed = r;
            readers[r] = new Thread(() =>
            {
                System.Random random = new(seed);
                while (Volatile.Read(ref done) == 0)
                {
                    int published = format.SectionCount;
                    int s = random.Next(published);
                    long offset = starts[s] + random.Next(28);
                    bool ok = format.TryFindSection(offset, out ushort index, out SectionInfo? section)
                        && index == s
                        && section.StartOffset == starts[s];
                    for (int i = 0; ok && section is not null && i < section.InterfaceCount; i++)
                    {
                        ok = section.Interface(i)?.LinkType == _LinkTypes[i];
                    }

                    if (!ok)
                    {
                        Interlocked.Increment(ref failures);
                    }

                    Interlocked.Increment(ref lookups);
                }
            })
            { Name = $"Section reader {r}", IsBackground = true };
            readers[r].Start();
        }

        while (scanner.NextFrame(out _) || !scanner.IsExhausted)
        {
            // Scan the whole file on this thread; readers run meanwhile.
        }

        Volatile.Write(ref done, 1);
        foreach (Thread reader in readers)
        {
            reader.Join();
        }

        // Assert
        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(lookups).IsGreaterThan(0);
        await Assert.That(format.SectionCount).IsEqualTo(_SectionCount);
        await Assert.That(format.TryFindSection(-1, out _, out _)).IsFalse();
        await Assert.That(format.TryFindSection(starts[^1] + 1_000_000, out ushort last, out _) && last == _SectionCount - 1).IsTrue();
    }

    #endregion

    #region Helpers

    /// <summary>SHB, three IDBs (Ethernet, SocketCAN, LIN), and two EPBs in the given byte order.</summary>
    private static byte[] _Section(bool bigEndian, int firstPayload)
    {
        List<byte> bytes = [];
        byte[] shb = new byte[28];
        _Write32(shb, 0, 0x0A0D_0D0A, bigEndian);
        _Write32(shb, 4, 28, bigEndian);
        _Write32(shb, 8, 0x1A2B_3C4D, bigEndian);
        _Write16(shb, 12, 1, bigEndian);
        _Write32(shb, 16, 0xFFFF_FFFF, bigEndian);
        _Write32(shb, 20, 0xFFFF_FFFF, bigEndian);
        _Write32(shb, 24, 28, bigEndian);
        bytes.AddRange(shb);

        foreach (LinkType linkType in _LinkTypes)
        {
            byte[] idb = new byte[20];
            _Write32(idb, 0, 1, bigEndian);
            _Write32(idb, 4, 20, bigEndian);
            _Write16(idb, 8, (ushort)linkType, bigEndian);
            _Write32(idb, 12, 65_535, bigEndian);
            _Write32(idb, 16, 20, bigEndian);
            bytes.AddRange(idb);
        }

        for (int p = 0; p < 2; p++)
        {
            byte[] epb = new byte[32 + 16];
            _Write32(epb, 0, 6, bigEndian);
            _Write32(epb, 4, 48, bigEndian);
            _Write32(epb, 20, 16, bigEndian);
            _Write32(epb, 24, 16, bigEndian);
            epb[28] = (byte)firstPayload;
            _Write32(epb, 44, 48, bigEndian);
            bytes.AddRange(epb);
        }

        return [.. bytes];
    }

    private static void _Write32(byte[] target, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), value);
        }
    }

    private static void _Write16(byte[] target, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset), value);
        }
    }

    #endregion
}
