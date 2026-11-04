// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// A nested protocol that records what it was handed: the bytes, whether they are a slice of one of the packet's buffers
/// (<see cref="MutField.TryGetEffectLayerKey"/> succeeds), and in which buffer. It consumes everything it gets, so the
/// protocol that dispatched to it counts the payload as taken.
/// <para>
/// Why a probe: a stateful nested protocol (the reassemblers, the stream trackers) keys its recorded effects on the slice
/// location and refuses data that is not a packet slice. This probe applies exactly that check and reports the result, so a
/// test can show that a protocol hands derived bytes (decoded, unmasked, inflated) to its nested protocol as a packet slice
/// without depending on one specific stateful protocol. <see cref="Register"/> binds it where the HTTP and WebSocket
/// dispatches look up their payload protocol: the <c>http.content_type</c> table under <see cref="ContentType"/> and the
/// <c>ws.port</c> table under key 0.
/// </para>
/// <para>
/// <b>Thread safety:</b> observations are appended to a concurrent queue, so one probe instance may be invoked from several
/// threads at once (the reparse tests); reading the queue is meant for the single test thread after the parses.
/// </para>
/// </summary>
internal sealed class BoundSliceProbeProtocol : IProtocol
{
    /// <summary>The <c>Content-Type</c> value under which the probe is registered in the HTTP content-type table.</summary>
    internal const string ContentType = "application/x-probe";

    /// <summary>
    /// What one call of the probe saw.
    /// </summary>
    /// <param name="Bytes">A copy of the bytes the probe was handed.</param>
    /// <param name="IsPacketSlice"><see langword="true"/> when the bytes are a slice of the frame or of a bound buffer.</param>
    /// <param name="BufferIndex">
    /// The packet buffer that holds the slice (0 = frame, 1.. = additional); -1 when it is not a packet slice.
    /// </param>
    internal readonly record struct Observation(byte[] Bytes, bool IsPacketSlice, int BufferIndex);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Observation> _Observations = new();

    /// <inheritdoc/>
    public string Name => "bound.slice.probe";

    /// <inheritdoc/>
    public string UiName => "Bound Slice Probe";

    /// <summary>The observations in call order.</summary>
    internal IReadOnlyCollection<Observation> Observations => _Observations;

    /// <summary>
    /// Registers a probe in the HTTP content-type table (<see cref="ContentType"/>) and in the WebSocket payload table
    /// (<c>ws.port</c>, key 0) of the stack that <paramref name="builder"/> builds. Call it after the standard protocols are
    /// registered: both tables are declared by those protocols.
    /// </summary>
    /// <param name="builder">The stack builder with the standard protocols registered.</param>
    /// <param name="probe">The probe to register.</param>
    internal static void Register(StackBuilder builder, BoundSliceProbeProtocol probe)
    {
        _ = builder.RegisterProtocol(probe, static (b, id, _) =>
        {
            b.RegisterParserInStringTableByName(HttpProtocol.ContentTypeTableName, ContentType, id);
            b.RegisterParserInU64TableByName(WebSocketProtocol.PortTableName, 0, id);
        });
    }

    /// <summary>
    /// Builds a stack with the standard protocols, the probe registered by <see cref="Register"/>, and whatever extra
    /// protocols <paramref name="configure"/> registers after them.
    /// </summary>
    /// <param name="probe">The probe to register.</param>
    /// <param name="configure">Registers further test protocols; <see langword="null"/> for none.</param>
    /// <returns>The built stack; the caller disposes it.</returns>
    internal static Stack BuildStack(BoundSliceProbeProtocol probe, Action<StackBuilder>? configure = null)
    {
        using SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Register(builder, probe);
        configure?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>
    /// Builds a stack like <see cref="BuildStack"/> whose settings start with the given values, so a protocol reads them when it
    /// registers (the way <c>ProtocolTestHelper.BuildStackWithSettings</c> does for the standard stack).
    /// </summary>
    /// <param name="probe">The probe to register.</param>
    /// <param name="settingOverrides">Setting names and the values to preload.</param>
    /// <returns>The built stack; the caller disposes it.</returns>
    internal static Stack BuildStackWithSettings(BoundSliceProbeProtocol probe, params (string Name, SettingValue Value)[] settingOverrides)
    {
        // Like BuildStack: the manager is disposed when the stack is built. The preloaded values are applied while the protocols
        // register, so they are in the protocols' fields by then and the stack does not read the manager again.
        using SettingsManager settings = new();
        foreach ((string name, SettingValue value) in settingOverrides)
        {
            settings.PreloadValue(name, value);
        }

        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        ProtocolRegistration.RegisterStandardProtocols(builder);
        Register(builder, probe);
        return builder.Build();
    }

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The same check a stateful nested protocol makes before it records an effect: the bytes must be locatable in the
        // packet's buffers. The buffer index sits in bits 31..24 of the key.
        bool isSlice = parentField.TryGetEffectLayerKey(data, out int key);
        int bufferIndex = isSlice
            ? (key >>> 24) & 0xFF
            : -1;
        _Observations.Enqueue(new Observation(data.ToArray(), isSlice, bufferIndex));

        return data.Length;
    }
}
