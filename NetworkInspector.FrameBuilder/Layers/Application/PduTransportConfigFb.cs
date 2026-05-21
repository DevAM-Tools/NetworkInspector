// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// FrameBuilder-side single source of truth for an AUTOSAR PDU-Transport
/// configuration. Mirrors what the parser receives via the
/// <c>pdu_transport.config_file</c> JSON plus the
/// <c>pdu_transport.id_field_size</c> / <c>pdu_transport.length_field_size</c>
/// settings. The same definition feeds three consumers in a test:
/// the FrameBuilder layer (wire format), the parser settings/JSON (decode
/// rule) and the tshark UAT profile (reference dissector).
/// </summary>
/// <remarks>
/// The type carries its own <c>Fb</c> suffix to disambiguate from the
/// parser-side <c>NetworkInspector.Protocols.PduTransport.PduTransportConfig</c>;
/// both representations are intentionally separate so the FrameBuilder does
/// not depend on the parser project.
/// <para>Thread safety: instances are immutable after construction.</para>
/// </remarks>
public sealed class PduTransportConfigFb
{
    private readonly byte _IdFieldSize;
    private readonly byte _LengthFieldSize;

    /// <summary>
    /// Creates a configuration with explicit ID and Length field sizes.
    /// </summary>
    /// <param name="idFieldSize">Size of the PDU-ID field in bytes; allowed values are 1, 2 and 4.</param>
    /// <param name="lengthFieldSize">Size of the Length field in bytes; allowed values are 1, 2 and 4.</param>
    /// <param name="pdus">PDU-ID → name registry passed through to the parser config and the UAT profile.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="idFieldSize"/> or <paramref name="lengthFieldSize"/> is not 1, 2 or 4.
    /// </exception>
    public PduTransportConfigFb(byte idFieldSize, byte lengthFieldSize, ImmutableArray<PduEntry> pdus)
    {
        if (idFieldSize is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(idFieldSize), idFieldSize, "PDU-Transport ID field size must be 1, 2 or 4 bytes.");
        }
        if (lengthFieldSize is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(lengthFieldSize), lengthFieldSize, "PDU-Transport Length field size must be 1, 2 or 4 bytes.");
        }
        _IdFieldSize = idFieldSize;
        _LengthFieldSize = lengthFieldSize;
        Pdus = pdus.IsDefault ? [] : pdus;
    }

    /// <summary>
    /// Creates a configuration with default 4-byte ID and Length field sizes.
    /// </summary>
    /// <param name="pdus">PDU-ID → name registry passed through to the parser config and the UAT profile.</param>
    public PduTransportConfigFb(ImmutableArray<PduEntry> pdus)
        : this(idFieldSize: 4, lengthFieldSize: 4, pdus)
    {
    }

    /// <summary>Size of the PDU-ID field in bytes (1, 2 or 4).</summary>
    public byte IdFieldSize => _IdFieldSize;

    /// <summary>Size of the Length field in bytes (1, 2 or 4).</summary>
    public byte LengthFieldSize => _LengthFieldSize;

    /// <summary>Registered PDU-ID → name mappings.</summary>
    public ImmutableArray<PduEntry> Pdus
    {
        get;
    }
}

/// <summary>
/// One entry in a <see cref="PduTransportConfigFb"/>: maps a PDU identifier
/// to its human-readable name.
/// </summary>
public readonly struct PduEntry
{
    /// <summary>Numeric PDU identifier.</summary>
    public uint PduId
    {
        get; init;
    }

    /// <summary>Human-readable PDU name (also written into parser/UAT config).</summary>
    public string Name
    {
        get; init;
    }
}
