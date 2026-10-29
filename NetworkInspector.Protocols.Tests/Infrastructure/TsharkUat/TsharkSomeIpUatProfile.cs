// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;

/// <summary>
/// Writes the external dissector's SOME/IP configuration tables (parameter lists, base types, strings, arrays, structures, unions,
/// enumerations and bitfields) from a compiled SOME/IP model, so that the parser and the tool decode the same bytes under one
/// description. Generating both sides from one configuration removes the hand-maintained duplicate that would otherwise drift.
/// <para>
/// <b>One definition per usage.</b> The tool names a value by the filter string of the table row that declares it, so a type that is
/// used at two places would give two values the same name. The parser names a value by its usage site. The generator therefore writes
/// a fresh copy of every type for every site; each leaf gets its own filter string (<c>f1</c>, <c>f2</c>, ...) and
/// <see cref="FilterOf"/> maps the registered name of the parser's site to it. The elements of an array share the filter string of the
/// array, which is how the tool names them; the parser's element site maps to the same filter.
/// </para>
/// <para>
/// <b>Mapping of the model.</b> A bool becomes an unsigned 8-bit number (the tool has no boolean), a lookup table becomes an
/// enumeration over the base type, an alias is resolved by the model, lengths are written in bits (the model counts bytes), a TLV
/// list or structure sets the tool's extension flag and numbers its members by data id, a union lists its alternatives by selector.
/// A UUID has no counterpart in the tool and throws, so a fixture never silently loses a value.
/// </para>
/// <para><b>Thread-safety:</b> an instance is built and read by one test; static helpers are stateless.</para>
/// </summary>
internal sealed class TsharkSomeIpUatProfile
{
    private const int _TypeBase = 1;
    private const int _TypeString = 2;
    private const int _TypeArray = 3;
    private const int _TypeStruct = 4;
    private const int _TypeUnion = 5;
    private const int _TypeEnum = 7;
    private const int _TypeBitfield = 8;

    private readonly SomeIpCompiledModel _Model;
    private readonly Dictionary<string, string> _FilterByName = [];
    private readonly StringBuilder _Lists = new();
    private readonly StringBuilder _BaseTypes = new();
    private readonly StringBuilder _Strings = new();
    private readonly StringBuilder _Arrays = new();
    private readonly StringBuilder _Structs = new();
    private readonly StringBuilder _Unions = new();
    private readonly StringBuilder _Enums = new();
    private readonly StringBuilder _Bitfields = new();
    private int _NextId = 1;
    private int _NextFilter = 1;

    private TsharkSomeIpUatProfile(SomeIpCompiledModel model) => _Model = model;

    /// <summary>The registered name of a parser site to the full filter name of the tool (<c>someip.payload.data.fN</c>, the name the tool gives a value that a table declares).</summary>
    /// <param name="registeredName">The registered field name of the parser.</param>
    internal string FilterOf(string registeredName) => "someip.payload.data." + _FilterByName[registeredName];

    /// <summary>Whether the parser's site has a counterpart in the tool.</summary>
    /// <param name="registeredName">The registered field name of the parser.</param>
    internal bool HasFilter(string registeredName) => _FilterByName.ContainsKey(registeredName);

    /// <summary>
    /// Creates the profile folder below <paramref name="personalRoot"/>, writes the tables of <paramref name="model"/> into it and
    /// returns the generator (for the name map) and the folder.
    /// </summary>
    /// <param name="personalRoot">The personal configuration root of the tool for this test.</param>
    /// <param name="runId">The name of the profile folder.</param>
    /// <param name="model">The compiled model.</param>
    /// <param name="profileDirectory">The folder that the tool is run with.</param>
    internal static TsharkSomeIpUatProfile Emit(string personalRoot, string runId, SomeIpCompiledModel model, out string profileDirectory)
    {
        profileDirectory = TsharkPduTransportSignalMessageUatProfile.CreateProfileDirectoryUnderEphemeralPersonalDir(personalRoot, runId);
        TsharkSomeIpUatProfile profile = new(model);
        profile._WriteLists();
        profile._Flush(profileDirectory);
        return profile;
    }

    #region Lists

    private void _WriteLists()
    {
        foreach (SomeIpServiceInfo service in _Model.Services.Span)
        {
            foreach (SomeIpMemberInfo member in service.Members.Span)
            {
                // Request and response (and the notification of an event or a notifier): the tool keys a list by message type.
                bool twoDirections = member.Kind is SomeIpMemberKind.Method or SomeIpMemberKind.FieldGetter or SomeIpMemberKind.FieldSetter;
                if (member.In.SitesCount > 0 || member.In.IsTlv)
                {
                    _WriteList(service, member, member.In, messageType: 0x00);
                }

                if (member.Out.SitesCount > 0 || member.Out.IsTlv)
                {
                    byte responseType = twoDirections
                        ? (byte)0x80
                        : (byte)0x02;
                    _WriteList(service, member, member.Out, messageType: responseType);
                }
            }
        }
    }

    private void _WriteList(SomeIpServiceInfo service, SomeIpMemberInfo member, in SomeIpParameterList list, byte messageType)
    {
        // The tool numbers a sequential list by position and a TLV list by data id; the count must exceed the greatest number.
        int count = list.SitesCount;
        if (list.IsTlv)
        {
            count = list.ById is { Count: > 0 } byId
                ? (int)byId.Keys.Max() + 1
                : 0;
        }

        for (int i = 0; i < list.SitesCount; i++)
        {
            SomeIpUsageSite site = _Model.Sites.Span[list.SitesStart + i];
            (int type, uint reference) = _EmitSite(site, member.Transform);
            string filter = _FilterOfSite(site);
            _ = _Lists.AppendLine(string.Join(
                ',',
                _Hex(service.ServiceId),
                _Hex(member.Id),
                _Dec(service.MajorVersion),
                _Hex(messageType),
                _Bool(list.IsTlv),
                _Dec(count),
                _Dec(site.Key),
                _Text(site.UiName),
                _Dec(type),
                _Hex(reference),
                _Text(filter)));
        }
    }

    #endregion

    #region Types

    /// <summary>
    /// Writes the definitions of one site and everything below it. Returns the tool's type code and the id of the definition.
    /// </summary>
    private (int Type, uint Id) _EmitSite(SomeIpUsageSite site, in SomeIpTransformation transform)
    {
        _ = _FilterOfSite(site);
        if (site.TypeIndex < 0)
        {
            throw new NotSupportedException($"The site '{site.RegisteredName}' has no type; the tool cannot decode it.");
        }

        SomeIpTypeInfo type = _Model.Types.Span[site.TypeIndex];
        switch (type.Category)
        {
            case SomeIpTypeCategory.Base when type.Primitive == SomeIpPrimitive.String:
                return (_TypeString, _EmitString(site, type, transform));
            case SomeIpTypeCategory.Base:
                return _EmitBase(site, type);
            case SomeIpTypeCategory.Array:
                return (_TypeArray, _EmitArray(site, type, transform));
            case SomeIpTypeCategory.Structure:
            case SomeIpTypeCategory.TlvStructure:
                return (_TypeStruct, _EmitStruct(site, type, transform));
            case SomeIpTypeCategory.Union:
                return (_TypeUnion, _EmitUnion(site, type, transform));
            case SomeIpTypeCategory.Bitfield:
                return (_TypeBitfield, _EmitBitfield(site, type));
            default:
                throw new NotSupportedException($"The type of '{site.RegisteredName}' ({type.Category}) has no counterpart in the tool.");
        }
    }

    private (int Type, uint Id) _EmitBase(SomeIpUsageSite site, SomeIpTypeInfo type)
    {
        (string name, int bits) = type.Primitive switch
        {
            SomeIpPrimitive.UInt8 or SomeIpPrimitive.Bool => ("uint8", 8),
            SomeIpPrimitive.UInt16 => ("uint16", 16),
            SomeIpPrimitive.UInt32 => ("uint32", 32),
            SomeIpPrimitive.UInt64 => ("uint64", 64),
            SomeIpPrimitive.Int8 => ("int8", 8),
            SomeIpPrimitive.Int16 => ("int16", 16),
            SomeIpPrimitive.Int32 => ("int32", 32),
            SomeIpPrimitive.Int64 => ("int64", 64),
            SomeIpPrimitive.Float => ("float32", 32),
            SomeIpPrimitive.Double => ("float64", 64),
            _ => throw new NotSupportedException($"The primitive {type.Primitive} of '{site.RegisteredName}' has no counterpart in the tool."),
        };
        uint id = _NewId();
        _ = _BaseTypes.AppendLine(string.Join(',', _Hex(id), _Text(site.RegisteredName), _Text(name), _Bool(!type.LittleEndian), _Dec(bits), _Dec(bits)));
        if (type.Lookup is not { Count: > 0 } lookup)
        {
            return (_TypeBase, id);
        }

        // A lookup table is an enumeration over the base type; its rows are sorted so the file is stable.
        uint enumId = _NewId();
        foreach (KeyValuePair<long, string> pair in lookup.OrderBy(static p => p.Key))
        {
            _ = _Enums.AppendLine(string.Join(
                ',', _Hex(enumId), _Text(site.RegisteredName), _Dec(_TypeBase), _Hex(id), _Dec(lookup.Count), _Hex64((ulong)pair.Key), _Text(pair.Value)));
        }

        return (_TypeEnum, enumId);
    }

    private uint _EmitString(SomeIpUsageSite site, SomeIpTypeInfo type, in SomeIpTransformation transform)
    {
        uint id = _NewId();
        bool dynamic = type.FixedLength <= 0;
        int lengthBytes = type.LengthSizeOverride > 0
            ? type.LengthSizeOverride
            : transform.StringLength;
        string encoding = type.Encoding switch
        {
            SomeIpStringEncoding.Ascii => "ascii",
            SomeIpStringEncoding.Utf16BigEndian or SomeIpStringEncoding.Utf16LittleEndian => "utf-16",
            _ => "utf-8",
        };
        bool bigEndian = type.Encoding != SomeIpStringEncoding.Utf16LittleEndian;
        int fixedLength = type.FixedLength;
        int lengthBits = lengthBytes * 8;
        if (dynamic)
        {
            fixedLength = 0;
        }
        else
        {
            lengthBits = 0;
        }

        _ = _Strings.AppendLine(string.Join(
            ',',
            _Hex(id),
            _Text(site.RegisteredName),
            _Text(encoding),
            _Bool(dynamic),
            _Dec(fixedLength),
            _Dec(lengthBits),
            _Bool(bigEndian),
            _Dec(0)));
        return id;
    }

    private uint _EmitArray(SomeIpUsageSite site, SomeIpTypeInfo type, in SomeIpTransformation transform)
    {
        SomeIpUsageSite element = _Model.Sites.Span[site.ChildrenStart];
        (int elementType, uint elementId) = _EmitSite(element, transform);

        // The elements are named by the array's own filter string in the tool; the parser's element site maps to it.
        _FilterByName[element.RegisteredName] = _FilterByName[site.RegisteredName];
        uint id = _NewId();
        bool dynamic = type.FixedCount <= 0;
        int lengthBytes = type.LengthSizeOverride > 0
            ? type.LengthSizeOverride
            : transform.ArrayLength;
        int lower = type.FixedCount;
        int upper = type.FixedCount;
        int lengthBits = 0;
        if (dynamic)
        {
            lower = Math.Max(type.MinElements, 0);
            upper = type.MaxElements >= 0
                ? type.MaxElements
                : 65535;
            lengthBits = lengthBytes * 8;
        }

        _ = _Arrays.AppendLine(string.Join(
            ',',
            _Hex(id),
            _Text(site.RegisteredName),
            _Dec(elementType),
            _Hex(elementId),
            _Dec(1),
            _Text(_FilterByName[site.RegisteredName]),
            _Dec(0),
            _Dec(lower),
            _Dec(upper),
            _Dec(lengthBits),
            _Dec(0)));
        return id;
    }

    private uint _EmitStruct(SomeIpUsageSite site, SomeIpTypeInfo type, in SomeIpTransformation transform)
    {
        bool tlv = type.Category == SomeIpTypeCategory.TlvStructure;
        List<(SomeIpUsageSite Member, int Type, uint Reference)> members = [];
        for (int i = 0; i < site.ChildrenCount; i++)
        {
            SomeIpUsageSite member = _Model.Sites.Span[site.ChildrenStart + i];
            (int memberType, uint reference) = _EmitSite(member, transform);
            members.Add((member, memberType, reference));
        }

        uint id = _NewId();
        int lengthBytes = type.LengthSizeOverride >= 0
            ? type.LengthSizeOverride
            : transform.StructLength;
        int count = tlv && members.Count > 0
            ? (int)members.Max(static m => m.Member.Key) + 1
            : members.Count;
        for (int i = 0; i < members.Count; i++)
        {
            (SomeIpUsageSite member, int memberType, uint reference) = members[i];
            uint position = (uint)i;
            if (tlv)
            {
                position = member.Key;
            }

            _ = _Structs.AppendLine(string.Join(
                ',',
                _Hex(id),
                _Text(site.RegisteredName),
                _Dec(lengthBytes * 8),
                _Dec(0),
                _Bool(tlv),
                _Dec(count),
                _Dec(position),
                _Text(member.UiName),
                _Dec(memberType),
                _Hex(reference),
                _Text(_FilterOfSite(member))));
        }

        return id;
    }

    private uint _EmitUnion(SomeIpUsageSite site, SomeIpTypeInfo type, in SomeIpTransformation transform)
    {
        List<(SomeIpUsageSite Alternative, int Type, uint Reference)> alternatives = [];
        for (int i = 0; i < site.ChildrenCount; i++)
        {
            SomeIpUsageSite alternative = _Model.Sites.Span[site.ChildrenStart + i];
            (int alternativeType, uint reference) = _EmitSite(alternative, transform);
            alternatives.Add((alternative, alternativeType, reference));
        }

        uint id = _NewId();
        foreach ((SomeIpUsageSite alternative, int alternativeType, uint reference) in alternatives)
        {
            _ = _Unions.AppendLine(string.Join(
                ',',
                _Hex(id),
                _Text(site.RegisteredName),
                _Dec(type.UnionLengthSize * 8),
                _Dec(type.UnionTypeSize * 8),
                _Dec(0),
                _Dec(alternatives.Count),
                _Dec(alternative.Key),
                _Text(alternative.UiName),
                _Dec(alternativeType),
                _Hex(reference),
                _Text(_FilterOfSite(alternative))));
        }

        return id;
    }

    private uint _EmitBitfield(SomeIpUsageSite site, SomeIpTypeInfo type)
    {
        uint id = _NewId();
        for (int i = 0; i < site.ChildrenCount; i++)
        {
            SomeIpUsageSite bit = _Model.Sites.Span[site.ChildrenStart + i];
            _ = _Bitfields.AppendLine(string.Join(
                ',',
                _Hex(id),
                _Text(site.RegisteredName),
                _Dec(type.Bits),
                _Dec(site.ChildrenCount),
                _Dec(bit.Key),
                _Text(bit.UiName),
                _Text(_FilterOfSite(bit))));
        }

        return id;
    }

    #endregion

    #region Output

    private void _Flush(string profileDirectory)
    {
        _Write(profileDirectory, "SOMEIP_parameter_list", _Lists);
        _Write(profileDirectory, "SOMEIP_parameter_base_types", _BaseTypes);
        _Write(profileDirectory, "SOMEIP_parameter_strings", _Strings);
        _Write(profileDirectory, "SOMEIP_parameter_arrays", _Arrays);
        _Write(profileDirectory, "SOMEIP_parameter_structs", _Structs);
        _Write(profileDirectory, "SOMEIP_parameter_unions", _Unions);
        _Write(profileDirectory, "SOMEIP_parameter_enums", _Enums);
        _Write(profileDirectory, "SOMEIP_parameter_bitfields", _Bitfields);
    }

    private static void _Write(string directory, string fileName, StringBuilder rows) =>
        File.WriteAllText(Path.Combine(directory, fileName), rows.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private uint _NewId() => (uint)_NextId++;

    /// <summary>The filter string of a site, allocated on first use.</summary>
    private string _FilterOfSite(SomeIpUsageSite site)
    {
        if (!_FilterByName.TryGetValue(site.RegisteredName, out string? filter))
        {
            filter = string.Create(CultureInfo.InvariantCulture, $"f{_NextFilter++}");
            _FilterByName[site.RegisteredName] = filter;
        }

        return filter;
    }

    private static string _Hex(uint value) => TsharkCsvUat.UatQuoted(TsharkCsvUat.Hex32Upper(value));

    private static string _Hex64(ulong value) => TsharkCsvUat.UatQuoted(value.ToString("X16", CultureInfo.InvariantCulture));

    private static string _Dec(int value) => TsharkCsvUat.UatQuoted(value);

    private static string _Dec(uint value) => TsharkCsvUat.UatQuoted(value);

    private static string _Bool(bool value) => TsharkCsvUat.UatQuoted(TsharkCsvUat.Bool(value));

    private static string _Text(string value) => TsharkCsvUat.UatQuoted(value);

    #endregion
}
