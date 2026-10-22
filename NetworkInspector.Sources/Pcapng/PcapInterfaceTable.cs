// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng;

/// <summary>
/// Registered identity of one PCAPNG / PCAP interface. Immutable; shared by the table and the interface's
/// <see cref="Format.InterfaceInfo"/>. A reference type on purpose: <see cref="Format.InterfaceInfo.SetRegistered"/>
/// publishes it with one compare-exchange into a <c>volatile</c> field that <c>FrameById</c> reads on any thread, which a
/// two-field struct could not do without tearing. One allocation per interface; interfaces are few.
/// </summary>
/// <param name="Id">Registry id of the interface.</param>
/// <param name="LinkType">Link type of the interface (open enum; undefined DLTs keep their number).</param>
internal sealed record PcapInterfaceEntry(FrameInterfaceId Id, LinkType LinkType);

/// <summary>
/// Maps <c>(section index, interface index)</c> to the registered interface of <see cref="PcapngSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why:</b> random access must resolve the registered interface (registry id and link type) of any frame the index
/// already holds, also while the lazy scan is still adding sections and interfaces on the <c>NextFrame</c> thread. The
/// format state (sections and their <see cref="InterfaceInfo"/>s) tells <c>FrameById</c> how to parse a block; this
/// table maps the parsed section and interface index to the registry entry.
/// </para>
/// <para>
/// <b>Thread-safety:</b> single writer (the thread that runs the scan or <c>Start</c>), any number of concurrent
/// readers. Both levels are <see cref="ChunkedGrowOnlyStore{T}"/>s, which publish the count after the slot is written,
/// so a reader that sees an index below the count sees a complete entry. The owner adds an interface before the scanner
/// indexes any packet of it, so every indexed frame resolves.
/// </para>
/// </remarks>
internal sealed class PcapInterfaceTable
{
    #region Fields

    /// <summary>Chunk shift 4 → 16 entries per chunk: interfaces and sections are few in normal captures.</summary>
    private const int _ChunkShift = 4;

    /// <summary>Per-section interface lists, indexed by section.</summary>
    private readonly ChunkedGrowOnlyStore<ChunkedGrowOnlyStore<PcapInterfaceEntry>> _Sections = new(_ChunkShift);

    #endregion

    #region Public API

    /// <summary>
    /// Adds the next interface of <paramref name="sectionIndex"/>. Single writer only. Interfaces of a section must be
    /// added in index order (the order the IDBs are read), sections in ascending order.
    /// </summary>
    /// <returns>False when the entry already exists (a repeated <c>Start</c>); nothing is changed then.</returns>
    /// <exception cref="InvalidOperationException">An interface index was skipped.</exception>
    internal bool TryAdd(ushort sectionIndex, ushort interfaceIndex, PcapInterfaceEntry entry)
    {
        // Sections without IDBs still get an (empty) list so the indexes stay aligned.
        while (_Sections.Count <= sectionIndex)
        {
            _Sections.Append(new ChunkedGrowOnlyStore<PcapInterfaceEntry>(_ChunkShift));
        }

        ChunkedGrowOnlyStore<PcapInterfaceEntry> interfaces = _Sections.Get(sectionIndex)!;
        if (interfaceIndex < interfaces.Count)
        {
            return false;
        }

        if (interfaceIndex != interfaces.Count)
        {
            throw new InvalidOperationException(
                $"Interface {interfaceIndex} of section {sectionIndex} added out of order (next is {interfaces.Count}).");
        }

        interfaces.Append(in entry);
        return true;
    }

    /// <summary>True when the entry exists. Safe from any thread.</summary>
    internal bool Contains(ushort sectionIndex, ushort interfaceIndex) => TryGet(sectionIndex, interfaceIndex, out _);

    /// <summary>Resolves an interface. Safe from any thread, concurrently with <see cref="TryAdd"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGet(ushort sectionIndex, ushort interfaceIndex, [NotNullWhen(true)] out PcapInterfaceEntry? entry)
    {
        if (_Sections.TryGet(sectionIndex, out ChunkedGrowOnlyStore<PcapInterfaceEntry> interfaces)
            && interfaces is not null
            && interfaces.TryGet(interfaceIndex, out entry)
            && entry is not null)
        {
            return true;
        }

        entry = null;
        return false;
    }

    #endregion
}
