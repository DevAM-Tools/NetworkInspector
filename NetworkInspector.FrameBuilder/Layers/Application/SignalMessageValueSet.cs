// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Mutable, per-signal value collection that pairs a <see cref="SignalMessageLayout"/>
/// with concrete values to encode. Keys are signal names (readable in tests);
/// internally an index cache is built on first render so the FrameBuilder hot
/// path needs no string hashing per signal.
/// </summary>
/// <remarks>
/// <para>
/// Two value flavours are supported:
/// </para>
/// <list type="bullet">
///   <item><see cref="Set(string, double)"/> stores a *physical* value;
///   the <see cref="SignalMessageLayer"/> applies the inverse linear scaling
///   <c>(physical − offset) / factor</c> and rounding before writeback.</item>
///   <item><see cref="SetRaw(string, ulong)"/> stores a *raw* bit value
///   verbatim — useful for value-name lookups, edge cases (max-unsigned,
///   floating-point NaN bit-patterns) and discrete enums.</item>
/// </list>
/// <para>
/// Mux-selector values are stored under the multiplexer's
/// <see cref="MuxSpec.Name"/>. The encoder consumes the selector value and
/// then renders only the matching <see cref="MuxGroupSpec"/>.
/// </para>
/// <para>
/// Thread safety: not thread-safe. A <see cref="SignalMessageValueSet"/> is built
/// per emitted frame and consumed once.
/// </para>
/// </remarks>
public sealed class SignalMessageValueSet
{
    private readonly SignalMessageLayout _Layout;
    private readonly Dictionary<string, double> _Physical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _Raw = new(StringComparer.Ordinal);

    /// <summary>
    /// Begins a fresh value set bound to <paramref name="layout"/>. The
    /// returned object is the builder; chain <see cref="Set(string, double)"/>
    /// / <see cref="SetRaw(string, ulong)"/> to populate it.
    /// </summary>
    public static SignalMessageValueSet For(SignalMessageLayout layout) => new(layout);

    private SignalMessageValueSet(SignalMessageLayout layout)
    {
        _Layout = layout;
    }

    /// <summary>The layout this value set was built for.</summary>
    public SignalMessageLayout Layout => _Layout;

    /// <summary>
    /// Stores a physical (post-scaled) value for the signal called
    /// <paramref name="signalName"/>. The encoder applies the inverse linear
    /// scaling <c>(physical − offset) / factor</c> before writeback.
    /// </summary>
    /// <returns>The same instance for fluent chaining.</returns>
    public SignalMessageValueSet Set(string signalName, double value)
    {
        _Physical[signalName] = value;
        _Raw.Remove(signalName);
        return this;
    }

    /// <summary>
    /// Stores a raw bit value for the signal called <paramref name="signalName"/>.
    /// The encoder writes the lowest <see cref="SignalSpec.BitLength"/> bits
    /// of <paramref name="raw"/> verbatim, bypassing scaling.
    /// </summary>
    /// <returns>The same instance for fluent chaining.</returns>
    public SignalMessageValueSet SetRaw(string signalName, ulong raw)
    {
        _Raw[signalName] = raw;
        _Physical.Remove(signalName);
        return this;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a raw value was stored for
    /// <paramref name="signalName"/> via <see cref="SetRaw(string, ulong)"/>.
    /// </summary>
    public bool TryGetRaw(string signalName, out ulong raw) => _Raw.TryGetValue(signalName, out raw);

    /// <summary>
    /// Returns <see langword="true"/> when a physical value was stored for
    /// <paramref name="signalName"/> via <see cref="Set(string, double)"/>.
    /// </summary>
    public bool TryGetPhysical(string signalName, out double physical) => _Physical.TryGetValue(signalName, out physical);
}
