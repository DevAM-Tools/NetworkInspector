// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Compact, immutable description of one signal for the parse hot path.
/// The members are declared with extraction and scaling first, then ids and text; the runtime is free to reorder the
/// fields of a struct that contains references, so the order is documentation, not a layout guarantee.
/// </summary>
/// <remarks>
/// Raw bits are always interpreted as an unsigned <see cref="ulong"/>; signed signals are not represented.
/// Physical value is <c>raw × factor + offset</c>, computed in <see cref="double"/>, so raw values above 2^53 lose
/// precision in the conversion (the raw value itself stays exact).
/// The struct is large, so the extraction and conversion helpers take it by <see langword="in"/> reference.
/// Thread safety: immutable value type; safe for concurrent reads (the referenced array and table are never
/// modified after compilation).
/// </remarks>
/// <param name="StartBit">
/// Bit position of the signal start (MSB for big-endian, LSB for little-endian). Bits are numbered inside a byte with
/// 0 as the least significant bit and the byte index is <c>StartBit / 8</c>, as in DBC files.
/// </param>
/// <param name="BitLength">Number of bits to extract (1–64).</param>
/// <param name="BigEndian"><see langword="true"/> for Motorola (big-endian) order; otherwise Intel (little-endian).</param>
/// <param name="Factor">Linear scaling factor: physical = raw × factor + offset.</param>
/// <param name="Offset">Linear scaling offset.</param>
/// <param name="SignalFieldId">Signal field id under the message container.</param>
/// <param name="RawFieldId">Optional raw child field; <see cref="FieldId.Invalid"/> when disabled.</param>
/// <param name="EnumFieldId">Optional enum child field; <see cref="FieldId.Invalid"/> when disabled.</param>
/// <param name="Name">Registered field name (JSON <c>name</c>; optional <c>.raw</c>/<c>.enum</c> suffixes are appended by the protocol).</param>
/// <param name="UiName">Human-readable signal name for CustomText.</param>
/// <param name="Unit">Physical unit string (may be empty).</param>
/// <param name="Enums">Discrete value-name table for this signal.</param>
/// <param name="CustomTextByRaw">
/// Precomputed CustomText indexed by raw value when <paramref name="BitLength"/> is ≤ 12 (4096 slots).
/// <see langword="null"/> for wider signals, which format on materialize.
/// </param>
internal readonly record struct SignalInfo(
    ushort StartBit,
    byte BitLength,
    bool BigEndian,
    double Factor,
    double Offset,
    FieldId SignalFieldId,
    FieldId RawFieldId,
    FieldId EnumFieldId,
    string Name,
    string UiName,
    string Unit,
    SignalEnumTable Enums,
    string[]? CustomTextByRaw);

/// <summary>
/// Compile-time mux group: selector value plus signals (machine names still on <see cref="SignalInfo"/>).
/// </summary>
/// <param name="MuxValue">Mux selector value.</param>
/// <param name="Signals">Signals active for this mux value.</param>
internal readonly record struct CompiledMuxGroup(ulong MuxValue, SignalInfo[] Signals);
