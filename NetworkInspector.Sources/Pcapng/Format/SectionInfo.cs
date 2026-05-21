// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format;

/// <summary>
/// Metadata about a PCAPNG section (started by a Section Header Block).
/// A file may contain multiple sections, each with its own byte order and interfaces.
/// </summary>
internal sealed class SectionInfo
{
    #region Properties

    /// <summary>Whether this section uses swapped byte order (big-endian file on LE host, or vice versa).</summary>
    internal bool ByteSwapped
    {
        get;
    }

    /// <summary>Interfaces declared in this section, indexed by their IDB order.</summary>
    private readonly List<InterfaceInfo> _Interfaces = [];

    /// <summary>Section length in bytes from the SHB, or -1 if unspecified.</summary>
    internal long SectionLength
    {
        get;
    }

    /// <summary>Hardware description from SHB option.</summary>
    internal string? Hardware
    {
        get; private set;
    }

    /// <summary>Operating system from SHB option.</summary>
    internal string? Os
    {
        get; private set;
    }

    /// <summary>User application from SHB option.</summary>
    internal string? UserApplication
    {
        get; private set;
    }

    /// <summary>File offset where this section starts (the SHB position).</summary>
    internal long StartOffset
    {
        get;
    }

    /// <summary>Number of interfaces in this section.</summary>
    internal int InterfaceCount => _Interfaces.Count;

    #endregion

    #region Constructors

    /// <summary>Creates a new section with the given byte order, section length, and file offset.</summary>
    internal SectionInfo(bool byteSwapped, long sectionLength, long startOffset)
    {
        ByteSwapped = byteSwapped;
        SectionLength = sectionLength;
        StartOffset = startOffset;
    }

    #endregion

    #region Internal API

    /// <summary>Adds an interface to this section. Returns the zero-based interface index.</summary>
    internal int AddInterface(InterfaceInfo info)
    {
        int index = _Interfaces.Count;
        _Interfaces.Add(info);
        return index;
    }

    /// <summary>Retrieves the interface at the given index, or null if out of range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal InterfaceInfo? Interface(int id) =>
        (uint)id < (uint)_Interfaces.Count ? _Interfaces[id] : null;

    /// <summary>
    /// Parses the options area of a Section Header Block and applies recognized options.
    /// </summary>
    internal void ParseShbOptions(ReadOnlySpan<byte> optionData)
    {
        PcapOptionIterator iter = new(optionData, ByteSwapped);
        while (iter.TryGetNext(out RawOption option))
        {
            switch (option.Code)
            {
                case PcapConstants.OptShbHardware:
                    Hardware = Encoding.UTF8.GetString(option.Value);
                    break;
                case PcapConstants.OptShbOs:
                    Os = Encoding.UTF8.GetString(option.Value);
                    break;
                case PcapConstants.OptShbUserAppl:
                    UserApplication = Encoding.UTF8.GetString(option.Value);
                    break;
            }
        }
    }

    /// <summary>
    /// Parses the options area of an Interface Description Block and builds an InterfaceInfo.
    /// </summary>
    internal InterfaceInfo ParseIdbOptions(ushort rawLinkType, uint snapLength, ReadOnlySpan<byte> optionData)
    {
        InterfaceInfo info = new(rawLinkType, snapLength);
        EndianReader reader = new(ByteSwapped);

        PcapOptionIterator iter = new(optionData, ByteSwapped);
        while (iter.TryGetNext(out RawOption option))
        {
            switch (option.Code)
            {
                case PcapConstants.OptIfName when option.Value.Length > 0:
                    info.SetName(Encoding.UTF8.GetString(option.Value));
                    break;
                case PcapConstants.OptIfDescription when option.Value.Length > 0:
                    info.SetDescription(Encoding.UTF8.GetString(option.Value));
                    break;
                case PcapConstants.OptIfSpeed when option.Value.Length == 8:
                    info.SetSpeed(reader.ReadU64(option.Value));
                    break;
                case PcapConstants.OptIfTsResol when option.Value.Length == 1:
                    info.SetTimestampResolution(option.Value[0]);
                    break;
                case PcapConstants.OptIfFilter when option.Value.Length > 1:
                    // First byte is the filter type, rest is the filter string
                    info.SetFilter(Encoding.UTF8.GetString(option.Value[1..]));
                    break;
                case PcapConstants.OptIfOs when option.Value.Length > 0:
                    info.SetOs(Encoding.UTF8.GetString(option.Value));
                    break;
                case PcapConstants.OptIfFcsLen when option.Value.Length == 1:
                    info.SetFcsLength(option.Value[0]);
                    break;
                case PcapConstants.OptIfTsOffset when option.Value.Length == 8:
                    info.SetTimestampOffset(reader.ReadI64(option.Value));
                    break;
            }
        }

        return info;
    }

    #endregion
}
