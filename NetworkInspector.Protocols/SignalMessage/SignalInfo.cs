// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.SignalMessage;

/// <summary>
/// Compact, immutable description of one signal for the parse hot path.
/// Layout keeps extraction/scaling members first for cache locality.
/// </summary>
/// <remarks>
/// Raw bits are always interpreted as an unsigned <see cref="ulong"/>.
/// Physical value is <c>raw × factor + offset</c>.
/// Thread safety: immutable value type; safe for concurrent reads.
/// </remarks>
/// <param name="StartBit">Bit position of the signal start (MSB for big-endian, LSB for little-endian).</param>
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
