// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SomeIp.Model;

/// <summary>
/// The immutable result of compiling a SOME/IP service description: services, members, types, usage sites, client names and
/// ports. Built once at registration by the compiler and read by every parse thread afterwards.
/// <para>
/// Lookups on the parse path are one frozen-dictionary probe for the service and one for the member; the payload decoder walks
/// the arrays by index. Frozen dictionaries are built once and are faster to read than a regular dictionary at the cost of a
/// slower build, which is the right trade for a table that is written once and read for every packet.
/// </para>
/// <para>
/// <b>Thread-safety:</b> immutable and safe to share, with one exception that is part of the registration protocol: the field
/// and group ids of the sites and members are assigned exactly once while the stack is built on a single thread, before any
/// packet is parsed. Rejected alternative: a second mutable "registration result" structure next to the model, which every
/// parse would have to consult in addition.
/// </para>
/// </summary>
public sealed class SomeIpCompiledModel
{
    #region Fields

    /// <summary>Key of a (service id, major version) pair: <c>serviceId &lt;&lt; 8 | major</c>, which fits an unsigned 24-bit value.</summary>
    private readonly FrozenDictionary<uint, int> _ServiceByKey;

    /// <summary>Service id to the first service with that id, for name resolution when the version has no definition.</summary>
    private readonly FrozenDictionary<ushort, int> _FirstServiceById;

    /// <summary>Client id to display name.</summary>
    private readonly FrozenDictionary<ushort, string> _ClientNames;

    #endregion

    #region Constructors

    private SomeIpCompiledModel()
    {
        _ServiceByKey = FrozenDictionary<uint, int>.Empty;
        _FirstServiceById = FrozenDictionary<ushort, int>.Empty;
        _ClientNames = FrozenDictionary<ushort, string>.Empty;
        Services = ReadOnlyMemory<SomeIpServiceInfo>.Empty;
        Types = ReadOnlyMemory<SomeIpTypeInfo>.Empty;
        Sites = ReadOnlyMemory<SomeIpUsageSite>.Empty;
        UdpPorts = ReadOnlyMemory<ushort>.Empty;
        TcpPorts = ReadOnlyMemory<ushort>.Empty;
        TlsPorts = ReadOnlyMemory<ushort>.Empty;
        DtlsPorts = ReadOnlyMemory<ushort>.Empty;
    }

    internal SomeIpCompiledModel(
        SomeIpServiceInfo[] services,
        SomeIpTypeInfo[] types,
        SomeIpUsageSite[] sites,
        Dictionary<ushort, string> clients,
        ushort[] udpPorts,
        ushort[] tcpPorts,
        ushort[] tlsPorts,
        ushort[] dtlsPorts)
    {
        Services = services;
        Types = types;
        Sites = sites;
        UdpPorts = udpPorts;
        TcpPorts = tcpPorts;
        TlsPorts = tlsPorts;
        DtlsPorts = dtlsPorts;
        _ClientNames = clients.ToFrozenDictionary();

        // The first definition of a (service, major) pair wins; the compiler already removed duplicates with a warning, so the
        // key is unique here. The id-only table keeps the first version in file order for the name fallback.
        Dictionary<uint, int> byKey = new(services.Length);
        Dictionary<ushort, int> firstById = new(services.Length);
        for (int i = 0; i < services.Length; i++)
        {
            byKey[_Key(services[i].ServiceId, services[i].MajorVersion)] = i;
            _ = firstById.TryAdd(services[i].ServiceId, i);
        }

        _ServiceByKey = byKey.ToFrozenDictionary();
        _FirstServiceById = firstById.ToFrozenDictionary();
    }

    #endregion

    #region Properties

    /// <summary>A model without services, types, names or ports: the state of a stack without a configuration.</summary>
    public static SomeIpCompiledModel Empty { get; } = new();

    /// <summary>The services (one entry per service id and major version).</summary>
    public ReadOnlyMemory<SomeIpServiceInfo> Services { get; }

    /// <summary>The decoded types; usage sites refer to them by index.</summary>
    public ReadOnlyMemory<SomeIpTypeInfo> Types { get; }

    /// <summary>Every usage site of every payload; members and sites refer to them by index.</summary>
    public ReadOnlyMemory<SomeIpUsageSite> Sites { get; }

    /// <summary>Configured UDP ports.</summary>
    public ReadOnlyMemory<ushort> UdpPorts { get; }

    /// <summary>Configured TCP ports.</summary>
    public ReadOnlyMemory<ushort> TcpPorts { get; }

    /// <summary>Configured TLS plaintext hand-off ports.</summary>
    public ReadOnlyMemory<ushort> TlsPorts { get; }

    /// <summary>Configured DTLS plaintext hand-off ports.</summary>
    public ReadOnlyMemory<ushort> DtlsPorts { get; }

    #endregion

    #region Public API

    /// <summary>Finds the service definition of a service id and interface major version.</summary>
    /// <param name="serviceId">The service id of the message.</param>
    /// <param name="majorVersion">The interface version of the message.</param>
    /// <param name="serviceIndex">The index into <see cref="Services"/> on success.</param>
    /// <returns>True when that version is defined.</returns>
    public bool TryGetService(ushort serviceId, byte majorVersion, out int serviceIndex) =>
        _ServiceByKey.TryGetValue(_Key(serviceId, majorVersion), out serviceIndex);

    /// <summary>Finds the name of a service from any of its versions (the first in file order).</summary>
    /// <param name="serviceId">The service id.</param>
    /// <param name="name">The display name on success.</param>
    /// <returns>True when some version of the service is defined.</returns>
    public bool TryGetServiceName(ushort serviceId, [NotNullWhen(true)] out string? name)
    {
        if (_FirstServiceById.TryGetValue(serviceId, out int index))
        {
            name = Services.Span[index].Name;
            return true;
        }

        name = null;
        return false;
    }

    /// <summary>Finds the display name of a client id.</summary>
    /// <param name="clientId">The client id.</param>
    /// <param name="name">The display name on success.</param>
    /// <returns>True when the client is named.</returns>
    public bool TryGetClientName(ushort clientId, [NotNullWhen(true)] out string? name) =>
        _ClientNames.TryGetValue(clientId, out name);

    #endregion

    #region Private helpers

    private static uint _Key(ushort serviceId, byte majorVersion) => ((uint)serviceId << 8) | majorVersion;

    #endregion
}
