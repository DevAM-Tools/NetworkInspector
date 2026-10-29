// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Config;

/// <summary>
/// Compiles a deserialized SOME/IP service description into the immutable <see cref="SomeIpCompiledModel"/>: validation,
/// duplicate handling, cycle detection, name derivation and the per-usage expansion of the type graph. It runs once at
/// registration; the parse path never checks configuration consistency again.
/// <para>
/// <b>Failure model.</b> The compiler never throws for configuration content. Every defect is exactly one
/// <see cref="SettingsLoadWarning"/> that names the node (service, member, type, position) and the rule, and a defect never
/// aborts the rest of the file: a defective node is skipped or marked unavailable, everything else compiles. A runtime miss
/// (an unconfigured service, a dangling reference met while decoding) is not a load warning.
/// </para>
/// <para>
/// <b>Passes per service version.</b> (1) register the type names, (2) validate every definition once and collect its
/// references, (3) detect reference cycles with an iterative depth-first search (the graph may be large, so no recursion),
/// (4) build the type infos, (5) compile the members and expand the type graph per usage so that every occurrence gets its
/// own registered name. Global name uniqueness is resolved across the whole file (first wins, the later site gets a suffix).
/// </para>
/// <para><b>Thread-safety:</b> stateless entry point; every call uses its own state.</para>
/// </summary>
internal static class SomeIpConfigCompiler
{
    #region Constants

    /// <summary>
    /// Sites one service may expand to. Threat: a configuration that nests a large shared type in many members expands
    /// combinatorially (every usage is its own subtree), which stalls registration and exhausts memory. A guard constant and
    /// not a setting: it is far above any real interface and protects only against generated or hostile files. A service that
    /// hits it is refused with one warning that names this number (the service count and type count limits cannot bound the
    /// expansion, because the expansion multiplies them). Posture: secure by default; the author splits the service.
    /// </summary>
    private const int _MaxSitesPerService = 1_000_000;

    /// <summary>
    /// Nesting depth of the per-usage expansion. Threat: a long acyclic chain of structures (each member the next
    /// structure) would recurse as deep as the chain. The value equals the largest allowed <c>someip.max_type_depth</c>,
    /// because a deeper nesting could never be decoded anyway. A site beyond it is unavailable and the service gets one warning.
    /// </summary>
    private const int _MaxExpansionDepth = 1_024;

    private const string _Group = "someip";
    private const string _Setting = "someip.config_file";

    #endregion

    #region Static tables

    /// <summary>The wire primitives by their configured names.</summary>
    private static readonly FrozenDictionary<string, SomeIpPrimitive> _Primitives = new Dictionary<string, SomeIpPrimitive>(StringComparer.Ordinal)
    {
        ["uint8"] = SomeIpPrimitive.UInt8,
        ["uint16"] = SomeIpPrimitive.UInt16,
        ["uint32"] = SomeIpPrimitive.UInt32,
        ["uint64"] = SomeIpPrimitive.UInt64,
        ["int8"] = SomeIpPrimitive.Int8,
        ["int16"] = SomeIpPrimitive.Int16,
        ["int32"] = SomeIpPrimitive.Int32,
        ["int64"] = SomeIpPrimitive.Int64,
        ["float"] = SomeIpPrimitive.Float,
        ["double"] = SomeIpPrimitive.Double,
        ["bool"] = SomeIpPrimitive.Bool,
        ["string"] = SomeIpPrimitive.String,
        ["uuid"] = SomeIpPrimitive.Uuid,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    #endregion

    #region Internal API

    /// <summary>
    /// Compiles a description. A hit of the file-level limits and the schema version are the loader's job
    /// (<see cref="SomeIpConfigLoader.TryAccept"/>); call it first.
    /// </summary>
    /// <param name="config">The deserialized description.</param>
    /// <param name="warnings">Receives one warning per defect, in file order.</param>
    /// <returns>The model; possibly with fewer services than the file.</returns>
    internal static SomeIpCompiledModel Compile(SomeIpConfig config, ICollection<SettingsLoadWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);
        return new Compilation(config, warnings).Run();
    }

    #endregion

    #region Compilation

    /// <summary>A usage site before it is expanded: the names, the reference and the key of one occurrence.</summary>
    private readonly record struct SiteSpec(string? Name, string? FilterName, string? UiName, string Reference, int PadTo, uint Key, string Describe);

    /// <summary>A type definition during compilation.</summary>
    private sealed class Def(SomeIpDataTypeConfig config, int position)
    {
        public SomeIpDataTypeConfig Config { get; } = config;

        public int Position { get; } = position;

        public string Name => Config.Name;

        public bool Unavailable { get; set; }

        public int TypeIndex { get; set; } = -1;

        public bool IsAlias => Config is SomeIpAliasTypeConfig;

        /// <summary>The validated child specs of a structure, TLV structure, union, bitfield or array (empty otherwise).</summary>
        public List<SiteSpec> Children { get; } = [];

        /// <summary>True when the children are addressed by key (TLV id, union selector, bit number).</summary>
        public bool Keyed { get; set; }

        /// <summary>The definitions this one references (for cycle detection).</summary>
        public List<Def> Edges { get; } = [];
    }

    /// <summary>One run of the compiler: owns every list that grows during compilation.</summary>
    private sealed class Compilation(SomeIpConfig config, ICollection<SettingsLoadWarning> warnings)
    {
        private readonly List<SomeIpTypeInfo> _Types = [];
        private readonly List<SomeIpUsageSite?> _Sites = [];
        private readonly List<SomeIpServiceInfo> _Services = [];
        private readonly Dictionary<string, int> _PrimitiveTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _UsedNames = new(StringComparer.Ordinal);
        private int _UnavailableType = -1;

        // Per-service state, reset by _CompileService.
        private readonly List<Def> _Defs = [];
        private readonly Dictionary<string, Def> _DefByName = new(StringComparer.Ordinal);
        private readonly List<SomeIpMemberInfo> _Members = [];
        private string _Where = "";
        private string _ServicePath = "";
        private int _SiteBase;
        private bool _SiteLimitHit;
        private bool _DepthWarned;

        internal SomeIpCompiledModel Run()
        {
            Dictionary<ushort, string> clients = _CompileClients();
            ushort[] udp = _CompilePorts(config.UdpPorts, "udp_ports");
            ushort[] tcp = _CompilePorts(config.TcpPorts, "tcp_ports");
            ushort[] tls = _CompilePorts(config.TlsPorts, "tls_ports");
            ushort[] dtls = _CompilePorts(config.DtlsPorts, "dtls_ports");

            // First definition of a (service id, major version) wins: a later duplicate is skipped with one warning that names
            // both positions, so the result never depends on which of two definitions the reader happens to look at first.
            Dictionary<uint, int> firstByKey = [];
            for (int i = 0; i < config.Services.Length; i++)
            {
                SomeIpServiceConfig service = config.Services[i];
                if (service is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"services[{i}] is null; it is skipped."));
                    continue;
                }

                if (service.ServiceId is < 0 or > 0xFFFE)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"services[{i}] ('{service.Name}'): service_id {service.ServiceId} is outside 0..65534 (65535 is reserved for service discovery)."));
                    continue;
                }

                if (service.MajorVersion is < 0 or > 255)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"services[{i}] ('{service.Name}'): major_version {service.MajorVersion} is outside 0..255."));
                    continue;
                }

                uint key = ((uint)service.ServiceId << 8) | (uint)service.MajorVersion;
                if (firstByKey.TryGetValue(key, out int first))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"services[{i}] repeats service 0x{service.ServiceId:X4} v{service.MajorVersion} of services[{first}]; the first is used."));
                    continue;
                }

                firstByKey[key] = i;
                _CompileService(service);
            }

            return new SomeIpCompiledModel(
                [.. _Services],
                [.. _Types],
                _SiteArray(),
                clients,
                udp,
                tcp,
                tls,
                dtls);
        }

        #region Clients and ports

        private Dictionary<ushort, string> _CompileClients()
        {
            Dictionary<ushort, string> clients = [];
            if (config.Clients is null)
            {
                return clients;
            }

            foreach (KeyValuePair<string, string> client in config.Clients)
            {
                if (!ushort.TryParse(client.Key, NumberStyles.None, CultureInfo.InvariantCulture, out ushort id))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"clients: '{client.Key}' is not a client id (decimal 0..65535)."));
                    continue;
                }

                if (string.IsNullOrEmpty(client.Value))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"clients: client {id} has an empty name."));
                    continue;
                }

                clients[id] = client.Value;
            }

            return clients;
        }

        private ushort[] _CompilePorts(ushort[] ports, string listName)
        {
            List<ushort> result = [];
            HashSet<ushort> seen = [];
            foreach (ushort port in ports)
            {
                if (port == 0)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{listName}: port 0 is not a port (1..65535)."));
                    continue;
                }

                if (!seen.Add(port))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{listName}: port {port} is listed twice; it is registered once."));
                    continue;
                }

                result.Add(port);
            }

            return [.. result];
        }

        #endregion

        #region Service

        private void _CompileService(SomeIpServiceConfig service)
        {
            _Where = string.Create(CultureInfo.InvariantCulture, $"Service 0x{service.ServiceId:X4} v{service.MajorVersion} ('{service.Name}')");
            _ServicePath = string.Concat(SomeIpNameTokens.Prefix, ".", SomeIpNameTokens.Service(service.Name, (byte)service.MajorVersion));
            _Defs.Clear();
            _DefByName.Clear();
            _Members.Clear();
            _SiteBase = _Sites.Count;
            _SiteLimitHit = false;
            _DepthWarned = false;

            // Names claimed by this service are released again when the service is refused, so a refused service does not
            // block the names of the services after it.
            int typeBase = _Types.Count;

            _RegisterDefs(service);
            foreach (Def def in _Defs)
            {
                _ValidateDef(def);
            }

            _DetectCycles();
            _BuildTypeInfos();

            Dictionary<ushort, int> memberById = [];
            Dictionary<ushort, string> eventGroups = [];
            Dictionary<ushort, int> idPosition = [];
            Dictionary<ushort, int> groupPosition = [];
            for (int i = 0; i < service.Methods.Length; i++)
            {
                if (service.Methods[i] is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"{_Where}: methods[{i}] is null; it is skipped."));
                    continue;
                }

                _CompileMember(i, service.Methods[i], memberById, eventGroups, idPosition, groupPosition);
            }

            if (_SiteLimitHit)
            {
                // The service is refused as a whole: its sites are removed and the names they claimed are released.
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_Where}: more than {_MaxSitesPerService} usage sites after expansion; the service is not registered."));
                for (int s = _SiteBase; s < _Sites.Count; s++)
                {
                    if (_Sites[s] is { } site)
                    {
                        _ = _UsedNames.Remove(site.RegisteredName);
                    }
                }

                _Sites.RemoveRange(_SiteBase, _Sites.Count - _SiteBase);
                _Types.RemoveRange(typeBase, _Types.Count - typeBase);
                _PrimitiveTypes.Clear();
                _UnavailableType = -1;
                return;
            }

            _Services.Add(new SomeIpServiceInfo
            {
                ServiceId = (ushort)service.ServiceId,
                MajorVersion = (byte)service.MajorVersion,
                Name = service.Name,
                Token = SomeIpNameTokens.Service(service.Name, (byte)service.MajorVersion),
                Members = _Members.ToArray(),
                MemberById = memberById.ToFrozenDictionary(),
                EventGroupNames = eventGroups.ToFrozenDictionary(),
            });
        }

        #endregion

        #region Type definitions

        private void _RegisterDefs(SomeIpServiceConfig service)
        {
            for (int i = 0; i < service.DataTypes.Length; i++)
            {
                SomeIpDataTypeConfig type = service.DataTypes[i];
                if (type is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"{_Where}: data_types[{i}] is null; it is skipped."));
                    continue;
                }

                if (string.IsNullOrEmpty(type.Name))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_Where}: data_types[{i}] has no name; it is skipped."));
                    continue;
                }

                if (_Primitives.ContainsKey(type.Name))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_Where}: data_types[{i}] ('{type.Name}') is named like a wire primitive; it is skipped."));
                    continue;
                }

                if (_DefByName.TryGetValue(type.Name, out Def? existing))
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_Where}: data_types[{i}] repeats the type name '{type.Name}' of data_types[{existing.Position}]; the first definition is used."));
                    continue;
                }

                Def def = new(type, i);
                _Defs.Add(def);
                _DefByName[type.Name] = def;
            }
        }

        private string _TypeWhere(Def def) => string.Create(CultureInfo.InvariantCulture, $"{_Where}, type '{def.Name}'");

        private void _ValidateDef(Def def)
        {
            string where = _TypeWhere(def);
            switch (def.Config)
            {
                case SomeIpBaseTypeConfig b:
                    _ValidateBase(def, b, where);
                    break;
                case SomeIpArrayTypeConfig a:
                    _ValidateArray(def, a, where);
                    break;
                case SomeIpStructureTypeConfig s:
                    _ValidateStructure(def, s, where);
                    break;
                case SomeIpTlvStructureTypeConfig t:
                    def.Keyed = true;
                    _CollectKeyed(def, t.Members, where, 0, 4095, "TLV id");
                    break;
                case SomeIpUnionTypeConfig u:
                    _ValidateUnion(def, u, where);
                    break;
                case SomeIpBitfieldTypeConfig bf:
                    _ValidateBitfield(def, bf, where);
                    break;
                case SomeIpAliasTypeConfig al:
                    _LinkReference(def, al.TypeReference, where, "alias target");
                    break;
                default:
                    break;
            }
        }

        private void _ValidateBase(Def def, SomeIpBaseTypeConfig b, string where)
        {
            if (!_Primitives.TryGetValue(b.TypeReference, out SomeIpPrimitive primitive))
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: unknown primitive '{b.TypeReference}'; the type is unavailable."));
                def.Unavailable = true;
                return;
            }

            _CheckPad(b.PadTo, where);
            bool isString = primitive == SomeIpPrimitive.String;
            bool multiByteNumeric = primitive is SomeIpPrimitive.UInt16 or SomeIpPrimitive.UInt32 or SomeIpPrimitive.UInt64
                or SomeIpPrimitive.Int16 or SomeIpPrimitive.Int32 or SomeIpPrimitive.Int64 or SomeIpPrimitive.Float or SomeIpPrimitive.Double;
            if (b.ByteOrder is not null && !multiByteNumeric)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: byte_order is not allowed on {b.TypeReference} (multi-byte numeric primitives only); it is ignored."));
            }
            else if (b.ByteOrder is not null && b.ByteOrder is not "big_endian" and not "little_endian")
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: byte_order '{b.ByteOrder}' is not big_endian or little_endian."));
            }

            if (!isString && (b.Encoding is not null || b.FixedLength is not null || b.SizeOfLength is not null))
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: encoding, fixed_length and size_of_length are allowed on the string primitive only; they are ignored."));
            }

            if (isString)
            {
                if (b.Encoding is not null && b.Encoding is not "utf-8" and not "ascii" and not "utf-16be" and not "utf-16le")
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: encoding '{b.Encoding}' is not utf-8, ascii, utf-16be or utf-16le."));
                }

                if (b.FixedLength is < 0)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: fixed_length {b.FixedLength} is negative."));
                }

                if (b.SizeOfLength is { } sizeOfLength and not 1 and not 2 and not 4)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: size_of_length {sizeOfLength} is not 1, 2 or 4."));
                }
            }
        }

        private void _ValidateArray(Def def, SomeIpArrayTypeConfig a, string where)
        {
            _CheckPad(a.PadTo, where);
            if (a.Size is < 0 || a.MinElements is < 0 || a.MaxElements is < 0)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: size, min_elements and max_elements must not be negative."));
            }

            if (a.SizeOfLength is { } sizeOfLength and not 1 and not 2 and not 4)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: size_of_length {sizeOfLength} is not 1, 2 or 4."));
            }

            _LinkReference(def, a.TypeReference, where, "element type");
            def.Children.Add(new SiteSpec(SomeIpNameTokens.ArrayElement, null, null, a.TypeReference, 0, 0, "array element"));
        }

        private void _ValidateStructure(Def def, SomeIpStructureTypeConfig s, string where)
        {
            _CheckPad(s.PadTo, where);
            if (s.SizeOfLength is { } sizeOfLength and not 0 and not 1 and not 2 and not 4)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: size_of_length {sizeOfLength} is not 0, 1, 2 or 4."));
            }

            for (int i = 0; i < s.Members.Length; i++)
            {
                SomeIpUsageSiteConfig member = s.Members[i];
                if (member is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"{where}: members[{i}] is null; it is skipped."));
                    continue;
                }

                string memberWhere = string.Create(CultureInfo.InvariantCulture, $"{where}, members[{i}]");
                _CheckPad(member.PadTo, memberWhere);
                _LinkReference(def, member.TypeReference, memberWhere, "type");
                def.Children.Add(new SiteSpec(member.Name, member.FilterName, member.UiName, member.TypeReference, member.PadTo ?? 0, (uint)i, memberWhere));
            }
        }

        private void _ValidateUnion(Def def, SomeIpUnionTypeConfig u, string where)
        {
            def.Keyed = true;
            _CheckPad(u.PadTo, where);
            if (u.SizeOfLength is { } sizeOfLength and not 1 and not 2 and not 4)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: size_of_length {sizeOfLength} is not 1, 2 or 4."));
            }

            if (u.SizeOfType is { } sizeOfType and not 1 and not 2 and not 4)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: size_of_type {sizeOfType} is not 1, 2 or 4."));
            }

            _CollectKeyed(def, u.Members, where, 1, uint.MaxValue, "union selector");
        }

        private void _ValidateBitfield(Def def, SomeIpBitfieldTypeConfig bf, string where)
        {
            def.Keyed = true;
            if (bf.Bits is not 8 and not 16 and not 32 and not 64)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: bits {bf.Bits} is not 8, 16, 32 or 64; the type is unavailable."));
                def.Unavailable = true;
                return;
            }

            if (bf.ByteOrder is not null && bf.ByteOrder is not "big_endian" and not "little_endian")
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: byte_order '{bf.ByteOrder}' is not big_endian or little_endian."));
            }

            List<KeyValuePair<uint, SomeIpBitMemberConfig>> bits = [];
            foreach (KeyValuePair<string, SomeIpBitMemberConfig> member in bf.Members)
            {
                if (member.Value is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"{where}: bit member '{member.Key}' is null; it is skipped."));
                    continue;
                }

                if (!uint.TryParse(member.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint bit) || bit >= bf.Bits)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: bit key '{member.Key}' is not a bit number below {bf.Bits}; the member is skipped."));
                    continue;
                }

                bits.Add(new KeyValuePair<uint, SomeIpBitMemberConfig>(bit, member.Value));
            }

            bits.Sort(static (x, y) => x.Key.CompareTo(y.Key));
            foreach (KeyValuePair<uint, SomeIpBitMemberConfig> bit in bits)
            {
                def.Children.Add(new SiteSpec(
                    bit.Value.Name,
                    bit.Value.FilterName,
                    bit.Value.UiName,
                    "bool",
                    0,
                    bit.Key,
                    string.Create(CultureInfo.InvariantCulture, $"{where}, bit {bit.Key}")));
            }
        }

        /// <summary>Validates keyed members (TLV id, union selector), links their references and adds their specs in key order.</summary>
        private void _CollectKeyed(Def def, Dictionary<string, SomeIpUsageSiteConfig> members, string where, uint minimum, uint maximum, string keyKind)
        {
            List<KeyValuePair<uint, SomeIpUsageSiteConfig>> sorted = [];
            foreach (KeyValuePair<string, SomeIpUsageSiteConfig> member in members)
            {
                if (member.Value is null)
                {
                    _Warn(string.Create(CultureInfo.InvariantCulture, $"{where}: member '{member.Key}' is null; it is skipped."));
                    continue;
                }

                if (!uint.TryParse(member.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint key) || key < minimum || key > maximum)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: member key '{member.Key}' is not a valid {keyKind} ({minimum}..{maximum}); the member is skipped."));
                    continue;
                }

                sorted.Add(new KeyValuePair<uint, SomeIpUsageSiteConfig>(key, member.Value));
            }

            sorted.Sort(static (x, y) => x.Key.CompareTo(y.Key));
            foreach (KeyValuePair<uint, SomeIpUsageSiteConfig> member in sorted)
            {
                string memberWhere = string.Create(CultureInfo.InvariantCulture, $"{where}, member {member.Key}");
                _LinkReference(def, member.Value.TypeReference, memberWhere, "type");
                _CheckTlvFixedString(member.Value.TypeReference, memberWhere, keyKind == "TLV id");
                string? defaultName = null;
                if (keyKind == "TLV id")
                {
                    defaultName = SomeIpNameTokens.TlvDefault(member.Key);
                }

                string? name = member.Value.Name
                    ?? defaultName;
                def.Children.Add(new SiteSpec(name, member.Value.FilterName, member.Value.UiName, member.Value.TypeReference, 0, member.Key, memberWhere));
            }
        }

        /// <summary>Records a reference edge when the reference names a definition, or warns when it names nothing.</summary>
        private void _LinkReference(Def def, string reference, string where, string role)
        {
            if (_Primitives.ContainsKey(reference))
            {
                return;
            }

            if (_DefByName.TryGetValue(reference, out Def? target))
            {
                def.Edges.Add(target);
                return;
            }

            _Warn(string.Create(
                CultureInfo.InvariantCulture,
                $"{where}: the {role} reference '{reference}' names no primitive and no type of this service."));
        }

        private void _CheckPad(int? padTo, string where)
        {
            if (padTo is < 0 or > 64)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: pad_to {padTo} is outside 0..64; it is ignored."));
            }
        }

        /// <summary>
        /// A fixed-length string under a TLV tag has two competing extents (the type says how many bytes, the tag may supply
        /// a length); the format forbids the combination.
        /// </summary>
        private void _CheckTlvFixedString(string reference, string where, bool isTlv)
        {
            if (isTlv && _ResolveBase(reference) is { Primitive: SomeIpPrimitive.String, FixedLength: > 0 })
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: a fixed_length string cannot be a TLV member (the tag supplies the extent)."));
            }
        }

        private SomeIpBaseTypeConfig? _ResolveBaseConfig(string reference)
        {
            string current = reference;
            for (int hops = 0; hops <= _Defs.Count; hops++)
            {
                if (!_DefByName.TryGetValue(current, out Def? def))
                {
                    return null;
                }

                if (def.Config is SomeIpAliasTypeConfig alias)
                {
                    current = alias.TypeReference;
                    continue;
                }

                return def.Config as SomeIpBaseTypeConfig;
            }

            return null;
        }

        private (SomeIpPrimitive Primitive, int FixedLength)? _ResolveBase(string reference)
        {
            if (_ResolveBaseConfig(reference) is { } b && _Primitives.TryGetValue(b.TypeReference, out SomeIpPrimitive primitive))
            {
                return (primitive, b.FixedLength ?? 0);
            }

            return null;
        }

        #endregion

        #region Cycles

        /// <summary>
        /// Marks every type on a reference cycle unavailable, one warning per cycle, then lets unavailability flow through
        /// aliases and arrays (an alias of, or an array of, an unavailable type is unavailable too). Iterative depth-first
        /// search with an explicit stack: the graph can be large and a recursive walk would end the process.
        /// </summary>
        private void _DetectCycles()
        {
            int[] color = new int[_Defs.Count]; // 0 white, 1 gray (on the stack), 2 black
            Dictionary<Def, int> index = [];
            for (int i = 0; i < _Defs.Count; i++)
            {
                index[_Defs[i]] = i;
            }

            List<(Def Node, int Edge)> stack = [];
            int[] stackPosition = new int[_Defs.Count];
            foreach (Def root in _Defs)
            {
                if (color[index[root]] != 0)
                {
                    continue;
                }

                stack.Add((root, 0));
                color[index[root]] = 1;
                stackPosition[index[root]] = 0;
                while (stack.Count > 0)
                {
                    (Def node, int edge) = stack[^1];
                    if (edge >= node.Edges.Count)
                    {
                        color[index[node]] = 2;
                        stack.RemoveAt(stack.Count - 1);
                        continue;
                    }

                    stack[^1] = (node, edge + 1);
                    Def next = node.Edges[edge];
                    int nextIndex = index[next];
                    if (color[nextIndex] == 0)
                    {
                        color[nextIndex] = 1;
                        stackPosition[nextIndex] = stack.Count;
                        stack.Add((next, 0));
                    }
                    else if (color[nextIndex] == 1)
                    {
                        List<string> names = [];
                        for (int s = stackPosition[nextIndex]; s < stack.Count; s++)
                        {
                            names.Add(string.Concat("'", stack[s].Node.Name, "'"));
                            stack[s].Node.Unavailable = true;
                        }

                        string chain = string.Join(" -> ", names);
                        _Warn(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{_Where}: the types {chain} reference each other in a cycle; they are unavailable."));
                    }
                }
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (Def def in _Defs)
                {
                    if (def.Unavailable || def.Config is not (SomeIpAliasTypeConfig or SomeIpArrayTypeConfig))
                    {
                        continue;
                    }

                    string reference = def.Config is SomeIpAliasTypeConfig alias
                        ? alias.TypeReference
                        : ((SomeIpArrayTypeConfig)def.Config).TypeReference;
                    if (_DefByName.TryGetValue(reference, out Def? target) && target.Unavailable)
                    {
                        def.Unavailable = true;
                        changed = true;
                    }
                }
            }
        }

        #endregion

        #region Type infos

        private void _BuildTypeInfos()
        {
            foreach (Def def in _Defs)
            {
                if (def.Unavailable)
                {
                    def.TypeIndex = _UnavailableTypeIndex();
                    continue;
                }

                if (def.IsAlias)
                {
                    continue;
                }

                def.TypeIndex = _AddType(_BuildTypeInfo(def));
            }
        }

        private SomeIpTypeInfo _BuildTypeInfo(Def def)
        {
            switch (def.Config)
            {
                case SomeIpBaseTypeConfig b:
                    SomeIpPrimitive primitive = _Primitives[b.TypeReference];
                    return new SomeIpTypeInfo
                    {
                        Category = SomeIpTypeCategory.Base,
                        Name = def.Name,
                        Primitive = primitive,
                        LittleEndian = b.ByteOrder == "little_endian",
                        Encoding = b.Encoding switch
                        {
                            "ascii" => SomeIpStringEncoding.Ascii,
                            "utf-16be" => SomeIpStringEncoding.Utf16BigEndian,
                            "utf-16le" => SomeIpStringEncoding.Utf16LittleEndian,
                            _ => SomeIpStringEncoding.Utf8,
                        },
                        FixedLength = Math.Max(0, b.FixedLength ?? 0),
                        LengthSizeOverride = _LengthSize(b.SizeOfLength, allowZero: false),
                        PadTo = _Pad(b.PadTo),
                        Lookup = _BuildLookup(def, b, primitive),
                    };
                case SomeIpArrayTypeConfig a:
                    return new SomeIpTypeInfo
                    {
                        Category = SomeIpTypeCategory.Array,
                        Name = def.Name,
                        FixedCount = Math.Max(0, a.Size ?? 0),
                        MinElements = a.MinElements ?? -1,
                        MaxElements = a.MaxElements ?? -1,
                        LengthSizeOverride = _LengthSize(a.SizeOfLength, allowZero: false),
                        PadTo = _Pad(a.PadTo),
                    };
                case SomeIpStructureTypeConfig s:
                    return new SomeIpTypeInfo
                    {
                        Category = SomeIpTypeCategory.Structure,
                        Name = def.Name,
                        LengthSizeOverride = _LengthSize(s.SizeOfLength, allowZero: true),
                        PadTo = _Pad(s.PadTo),
                    };
                case SomeIpTlvStructureTypeConfig:
                    return new SomeIpTypeInfo { Category = SomeIpTypeCategory.TlvStructure, Name = def.Name };
                case SomeIpUnionTypeConfig u:
                    return new SomeIpTypeInfo
                    {
                        Category = SomeIpTypeCategory.Union,
                        Name = def.Name,
                        UnionLengthSize = _UnionFieldSize(u.SizeOfLength),
                        UnionTypeSize = _UnionFieldSize(u.SizeOfType),
                        PadTo = _Pad(u.PadTo),
                    };
                case SomeIpBitfieldTypeConfig bf:
                    return new SomeIpTypeInfo
                    {
                        Category = SomeIpTypeCategory.Bitfield,
                        Name = def.Name,
                        Bits = (byte)bf.Bits,
                        LittleEndian = bf.ByteOrder == "little_endian",
                    };
                default:
                    return new SomeIpTypeInfo { Category = SomeIpTypeCategory.Unavailable, Name = def.Name };
            }
        }

        private static sbyte _LengthSize(int? configured, bool allowZero)
        {
            if (configured is 1 or 2 or 4 || (allowZero && configured == 0))
            {
                return (sbyte)configured.Value;
            }

            return -1;
        }

        /// <summary>The size in bytes of the length field or the type selector of a union; the specification default of 4 when unset or not 1, 2 or 4.</summary>
        private static byte _UnionFieldSize(int? configured)
        {
            if (configured is 1 or 2 or 4)
            {
                return (byte)configured.Value;
            }

            return 4;
        }

        private static byte _Pad(int? padTo) =>
            padTo is >= 0 and <= 64
                ? (byte)padTo.Value
                : (byte)0;

        private FrozenDictionary<long, string>? _BuildLookup(Def def, SomeIpBaseTypeConfig b, SomeIpPrimitive primitive)
        {
            if (b.LookupValues is null)
            {
                return null;
            }

            string where = _TypeWhere(def);
            if (primitive is SomeIpPrimitive.Float or SomeIpPrimitive.Double or SomeIpPrimitive.String or SomeIpPrimitive.Uuid)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: lookup_values is not allowed on {b.TypeReference}; it is ignored."));
                return null;
            }

            bool signed = primitive is SomeIpPrimitive.Int8 or SomeIpPrimitive.Int16 or SomeIpPrimitive.Int32 or SomeIpPrimitive.Int64;
            Dictionary<long, string> lookup = [];
            foreach (KeyValuePair<string, string> entry in b.LookupValues)
            {
                long key;
                bool parsed = signed
                    ? long.TryParse(entry.Key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out key)
                    : _TryParseUnsigned(entry.Key, out key);
                if (!parsed)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{where}: lookup key '{entry.Key}' does not fit {b.TypeReference}; the entry is skipped."));
                    continue;
                }

                lookup[key] = entry.Value;
            }

            return lookup.ToFrozenDictionary();
        }

        private static bool _TryParseUnsigned(string text, out long key)
        {
            // Unsigned raw values above long.MaxValue keep their bit pattern; the decoder compares the same pattern.
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            {
                key = unchecked((long)value);
                return true;
            }

            key = 0;
            return false;
        }

        private int _AddType(SomeIpTypeInfo type)
        {
            _Types.Add(type);
            return _Types.Count - 1;
        }

        private int _PrimitiveType(string name)
        {
            if (!_PrimitiveTypes.TryGetValue(name, out int index))
            {
                index = _AddType(new SomeIpTypeInfo { Category = SomeIpTypeCategory.Base, Name = name, Primitive = _Primitives[name] });
                _PrimitiveTypes[name] = index;
            }

            return index;
        }

        private int _UnavailableTypeIndex()
        {
            if (_UnavailableType < 0)
            {
                _UnavailableType = _AddType(new SomeIpTypeInfo { Category = SomeIpTypeCategory.Unavailable, Name = "unavailable" });
            }

            return _UnavailableType;
        }

        #endregion

        #region Members

        private void _CompileMember(
            int position,
            SomeIpMemberConfig member,
            Dictionary<ushort, int> memberById,
            Dictionary<ushort, string> eventGroups,
            Dictionary<ushort, int> idPosition,
            Dictionary<ushort, int> groupPosition)
        {
            string where = string.Create(CultureInfo.InvariantCulture, $"{_Where}, methods[{position}] ('{member.Name}')");
            switch (member)
            {
                case SomeIpMethodConfig method:
                    if (_ClaimId(method.Id, position, where, idPosition, out ushort methodId))
                    {
                        _AddMember(
                            methodId,
                            SomeIpMemberKind.Method,
                            method.Name,
                            "",
                            method.In,
                            method.Out,
                            method.E2eProfile,
                            method.TransformationProperties,
                            where,
                            memberById,
                            direction: true);
                    }

                    break;
                case SomeIpEventConfig evt:
                    if (_ClaimId(evt.Id, position, where, idPosition, out ushort eventId))
                    {
                        _AddMember(
                            eventId,
                            SomeIpMemberKind.Event,
                            evt.Name,
                            "",
                            null,
                            _Single(evt.TypeReference),
                            evt.E2eProfile,
                            evt.TransformationProperties,
                            where,
                            memberById,
                            direction: false);
                    }

                    break;
                case SomeIpFieldConfig field:
                    _CompileField(position, field, where, memberById, idPosition);
                    break;
                case SomeIpEventGroupConfig group:
                    if (group.Id is < 0 or > 65535)
                    {
                        _Warn(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{where}: eventgroup id {group.Id} is outside 0..65535."));
                    }
                    else if (groupPosition.TryGetValue((ushort)group.Id, out int firstGroup))
                    {
                        _Warn(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{where}: eventgroup id {group.Id} repeats methods[{firstGroup}]; the first definition is used."));
                    }
                    else
                    {
                        groupPosition[(ushort)group.Id] = position;
                        eventGroups[(ushort)group.Id] = group.Name;
                    }

                    break;
                default:
                    break;
            }
        }

        private void _CompileField(int position, SomeIpFieldConfig field, string where, Dictionary<ushort, int> memberById, Dictionary<ushort, int> idPosition)
        {
            if (field.GetId is null && field.SetId is null && field.NotifierId is null)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: a field needs at least one of get_id, set_id and notifier_id; it is skipped."));
                return;
            }

            string token = SomeIpNameTokens.Token(field.Name);
            if (field.GetId is { } getId && _ClaimId(getId, position, where + " get_id", idPosition, out ushort get))
            {
                _AddMember(
                    get,
                    SomeIpMemberKind.FieldGetter,
                    field.Name,
                    token + "_get",
                    null,
                    _Single(field.TypeReference),
                    field.E2eProfile,
                    field.TransformationProperties,
                    where,
                    memberById,
                    direction: true);
            }

            if (field.SetId is { } setId && _ClaimId(setId, position, where + " set_id", idPosition, out ushort set))
            {
                _AddMember(
                    set,
                    SomeIpMemberKind.FieldSetter,
                    field.Name,
                    token + "_set",
                    _Single(field.TypeReference),
                    _Single(field.TypeReference),
                    field.E2eProfile,
                    field.TransformationProperties,
                    where,
                    memberById,
                    direction: true);
            }

            if (field.NotifierId is { } notifierId && _ClaimId(notifierId, position, where + " notifier_id", idPosition, out ushort notifier))
            {
                _AddMember(
                    notifier,
                    SomeIpMemberKind.FieldNotifier,
                    field.Name,
                    token + "_notifier",
                    null,
                    _Single(field.TypeReference),
                    field.E2eProfile,
                    field.TransformationProperties,
                    where,
                    memberById,
                    direction: false);
            }
        }

        private static SomeIpParameterListConfig _Single(string reference) =>
            new() { Sequential = [new SomeIpUsageSiteConfig { TypeReference = reference }] };

        /// <summary>Checks a wire id and claims it; a repeated id keeps the first definition and warns with both positions.</summary>
        private bool _ClaimId(int id, int position, string where, Dictionary<ushort, int> idPosition, out ushort claimed)
        {
            claimed = 0;
            if (id is < 0 or > 65535)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: id {id} is outside 0..65535; the member is skipped."));
                return false;
            }

            claimed = (ushort)id;
            if (idPosition.TryGetValue(claimed, out int first))
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: id {id} repeats methods[{first}]; the first definition is used."));
                return false;
            }

            idPosition[claimed] = position;
            return true;
        }

        private void _AddMember(
            ushort id,
            SomeIpMemberKind kind,
            string name,
            string tokenOverride,
            SomeIpParameterListConfig? inConfig,
            SomeIpParameterListConfig outConfig,
            string? e2eText,
            SomeIpTransformationConfig? transformation,
            string where,
            Dictionary<ushort, int> memberById,
            bool direction)
        {
            string token = tokenOverride.Length > 0
                ? tokenOverride
                : SomeIpNameTokens.Token(name);
            int memberIndex = _Members.Count;
            string memberPath = string.Concat(_ServicePath, ".", token);

            // The request direction first (empty for getters, events and notifiers), then the response direction. Their sites
            // are expanded into separate ranges because the same type at the request and the response is two fields.
            string inPath = direction
                ? string.Concat(memberPath, ".in")
                : memberPath;
            string outPath = direction
                ? string.Concat(memberPath, ".out")
                : memberPath;
            SomeIpParameterList inList = inConfig is null
                ? new SomeIpParameterList(false, _Sites.Count, 0, null)
                : _CompileList(inConfig, inPath, memberIndex, isOut: false, string.Concat(where, " in"));
            SomeIpParameterList outList = _CompileList(outConfig, outPath, memberIndex, isOut: true, string.Concat(where, " out"));

            SomeIpE2eProfile profile = e2eText switch
            {
                null or "" => SomeIpE2eProfile.None,
                "04" => SomeIpE2eProfile.Profile04,
                "44" => SomeIpE2eProfile.Profile44,
                "08" => SomeIpE2eProfile.Profile08,
                _ => SomeIpE2eProfile.Unknown,
            };
            if (profile == SomeIpE2eProfile.Unknown)
            {
                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: e2e_profile '{e2eText}' is not 04, 44 or 08; the payload is shown raw."));
            }

            _Members.Add(new SomeIpMemberInfo
            {
                Id = id,
                Kind = kind,
                Name = name,
                Token = token,
                In = inList,
                Out = outList,
                E2e = profile,
                E2eProfileText = e2eText ?? "",
                Transform = _ResolveTransformation(transformation, where),
            });
            memberById[id] = memberIndex;
        }

        private SomeIpTransformation _ResolveTransformation(SomeIpTransformationConfig? config, string where)
        {
            byte Length(int? configured, string property, bool structure)
            {
                if (configured is null)
                {
                    return 4;
                }

                if (configured is 1 or 2 or 4)
                {
                    return (byte)configured.Value;
                }

                if (configured == 0)
                {
                    // 0 means "format default" for strings and arrays but "no length field" for structures.
                    return structure
                        ? (byte)0
                        : (byte)4;
                }

                _Warn(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{where}: {property} {configured} is not 0, 1, 2 or 4; the default 4 is used."));
                return 4;
            }

            return config is null
                ? new SomeIpTransformation(4, 4, 4)
                : new SomeIpTransformation(
                    Length(config.SizeOfStringLength, "size_of_string_length", structure: false),
                    Length(config.SizeOfArrayLength, "size_of_array_length", structure: false),
                    Length(config.SizeOfStructLength, "size_of_struct_length", structure: true));
        }

        #endregion

        #region Sites

        private SomeIpParameterList _CompileList(SomeIpParameterListConfig list, string path, int memberIndex, bool isOut, string where)
        {
            List<SiteSpec> specs = [];
            bool tlv = list.Tlv is not null;
            if (list.Tlv is not null)
            {
                List<KeyValuePair<uint, SomeIpUsageSiteConfig>> sorted = [];
                foreach (KeyValuePair<string, SomeIpUsageSiteConfig> member in list.Tlv)
                {
                    if (member.Value is null)
                    {
                        _Warn(string.Create(CultureInfo.InvariantCulture, $"{where}: TLV member '{member.Key}' is null; it is skipped."));
                        continue;
                    }

                    if (!uint.TryParse(member.Key, NumberStyles.None, CultureInfo.InvariantCulture, out uint key) || key > 4095)
                    {
                        _Warn(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{where}: TLV key '{member.Key}' is not a TLV id (0..4095); the member is skipped."));
                        continue;
                    }

                    sorted.Add(new KeyValuePair<uint, SomeIpUsageSiteConfig>(key, member.Value));
                }

                sorted.Sort(static (x, y) => x.Key.CompareTo(y.Key));
                foreach (KeyValuePair<uint, SomeIpUsageSiteConfig> member in sorted)
                {
                    string memberWhere = string.Create(CultureInfo.InvariantCulture, $"{where} TLV member {member.Key}");
                    _CheckTlvFixedString(member.Value.TypeReference, memberWhere, isTlv: true);
                    specs.Add(new SiteSpec(
                        member.Value.Name ?? SomeIpNameTokens.TlvDefault(member.Key),
                        member.Value.FilterName,
                        member.Value.UiName,
                        member.Value.TypeReference,
                        member.Value.PadTo ?? 0,
                        member.Key,
                        memberWhere));
                }
            }
            else
            {
                SomeIpUsageSiteConfig[] sequential = list.Sequential ?? [];
                for (int i = 0; i < sequential.Length; i++)
                {
                    SomeIpUsageSiteConfig site = sequential[i];
                    if (site is null)
                    {
                        _Warn(string.Create(CultureInfo.InvariantCulture, $"{where} parameter {i} is null; it is skipped."));
                        continue;
                    }

                    specs.Add(new SiteSpec(
                        site.Name,
                        site.FilterName,
                        site.UiName,
                        site.TypeReference,
                        site.PadTo ?? 0,
                        (uint)i,
                        string.Create(CultureInfo.InvariantCulture, $"{where} parameter {i}")));
                }
            }

            _Expand(specs, path, memberIndex, isOut, depth: 0, topLevel: true, tlv, out int start, out int count, out FrozenDictionary<uint, int>? byKey);
            return new SomeIpParameterList(tlv, start, count, byKey);
        }

        /// <summary>
        /// Expands specs into usage sites: reserves the child range first so that the sites of one list are contiguous, then
        /// fills each site, expanding its own children (the children of site i are appended after the whole range).
        /// </summary>
        private void _Expand(
            List<SiteSpec> specs,
            string parentPath,
            int memberIndex,
            bool isOut,
            int depth,
            bool topLevel,
            bool keyed,
            out int start,
            out int count,
            out FrozenDictionary<uint, int>? byKey)
        {
            start = _Sites.Count;
            count = 0;
            byKey = null;

            if (_SiteLimitHit)
            {
                return;
            }

            if (depth > _MaxExpansionDepth)
            {
                if (!_DepthWarned)
                {
                    _DepthWarned = true;
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_Where}: types nest deeper than {_MaxExpansionDepth} levels; the deeper levels are not expanded."));
                }

                return;
            }

            if (_Sites.Count - _SiteBase + specs.Count > _MaxSitesPerService)
            {
                _SiteLimitHit = true;
                return;
            }

            count = specs.Count;
            for (int i = 0; i < specs.Count; i++)
            {
                _Sites.Add(null);
            }

            Dictionary<uint, int>? keyMap = keyed
                ? []
                : null;
            for (int i = 0; i < specs.Count; i++)
            {
                SiteSpec spec = specs[i];
                (int typeIndex, Def? concrete, string display) = _ResolveConcrete(spec.Reference);
                if (typeIndex < 0 && topLevel)
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{spec.Describe}: the type reference '{spec.Reference}' names no primitive and no type of this service."));
                }

                string token = SomeIpNameTokens.Token(spec.Name ?? display);
                string path = SomeIpNameTokens.Child(parentPath, token);
                string registered = _ClaimName(path, spec.FilterName, spec.Describe, out string childPath);
                string ui = spec.UiName ?? spec.Name ?? display;

                int childrenStart = _Sites.Count;
                int childrenCount = 0;
                FrozenDictionary<uint, int>? childByKey = null;
                if (concrete is not null && concrete.Children.Count > 0)
                {
                    _Expand(
                        concrete.Children,
                        childPath,
                        memberIndex,
                        isOut,
                        depth + 1,
                        topLevel: false,
                        concrete.Keyed,
                        out childrenStart,
                        out childrenCount,
                        out childByKey);
                }

                _Sites[start + i] = new SomeIpUsageSite
                {
                    TypeIndex = typeIndex,
                    ReferenceName = spec.Reference,
                    RegisteredName = registered,
                    UiName = ui,
                    PadTo = _Pad(spec.PadTo),
                    Key = spec.Key,
                    MemberIndex = memberIndex,
                    IsOut = isOut,
                    ChildrenStart = childrenStart,
                    ChildrenCount = childrenCount,
                    ChildByKey = childByKey,
                };
                keyMap?[spec.Key] = start + i;
            }

            byKey = keyMap?.ToFrozenDictionary();
        }

        /// <summary>
        /// Follows an alias chain to the type that decodes. Returns -1 for a dangling reference. The display name is the name
        /// that was referenced (an alias keeps its own name for display and for the default path token).
        /// </summary>
        private (int TypeIndex, Def? Concrete, string Display) _ResolveConcrete(string reference)
        {
            string current = reference;
            for (int hops = 0; hops <= _Defs.Count; hops++)
            {
                if (_Primitives.ContainsKey(current))
                {
                    return (_PrimitiveType(current), null, reference);
                }

                if (!_DefByName.TryGetValue(current, out Def? def))
                {
                    return (-1, null, reference);
                }

                if (def.Unavailable)
                {
                    return (_UnavailableTypeIndex(), null, reference);
                }

                if (def.Config is SomeIpAliasTypeConfig alias)
                {
                    current = alias.TypeReference;
                    continue;
                }

                return (def.TypeIndex, def, reference);
            }

            return (_UnavailableTypeIndex(), null, reference);
        }

        /// <summary>
        /// Claims the registered name of a site. Uniqueness is global across the file: the first site keeps the name, a later
        /// site that derives or declares the same name gets a numeric suffix and one warning that names both paths. The
        /// children of a renamed site continue from the renamed path, so one collision costs one warning.
        /// </summary>
        private string _ClaimName(string derivedPath, string? filterName, string describe, out string childPath)
        {
            string candidate = derivedPath;
            bool fromFilter = false;
            if (filterName is not null)
            {
                if (NameValidation.IsValidName(filterName))
                {
                    candidate = filterName;
                    fromFilter = true;
                }
                else
                {
                    _Warn(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{describe}: filter_name '{filterName}' is not a valid field name; the derived name is used."));
                }
            }

            if (_UsedNames.TryAdd(candidate, derivedPath))
            {
                childPath = derivedPath;
                return candidate;
            }

            string existing = _UsedNames[candidate];
            string final = candidate;
            for (int suffix = 2; ; suffix++)
            {
                final = string.Create(CultureInfo.InvariantCulture, $"{candidate}_{suffix}");
                if (_UsedNames.TryAdd(final, derivedPath))
                {
                    break;
                }
            }

            _Warn(string.Create(
                CultureInfo.InvariantCulture,
                $"{describe}: the registered name '{candidate}' of {derivedPath} is already used by {existing}; the later site is registered as '{final}'."));
            childPath = fromFilter
                ? derivedPath
                : final;
            return final;
        }

        #endregion

        /// <summary>The expanded sites as an array; every reserved slot was filled by the expansion that reserved it.</summary>
        private SomeIpUsageSite[] _SiteArray()
        {
            SomeIpUsageSite[] result = new SomeIpUsageSite[_Sites.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = _Sites[i]!; // the expansion fills every slot it reserves before it returns
            }

            return result;
        }

        private void _Warn(string message) =>
            warnings.Add(new SettingsLoadWarning(SettingsLoadWarningKind.DeserializationError, _Group, _Setting, message));
    }

    #endregion
}
