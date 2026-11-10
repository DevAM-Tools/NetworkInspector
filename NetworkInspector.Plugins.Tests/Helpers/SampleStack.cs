// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Builds stacks for the sample plugin tests: the standard protocols plus, optionally, the stack contribution of the sample
/// plugin exactly as a loader would run it. Stateless; every call creates a new stack.
/// </summary>
internal static class SampleStack
{
    #region Public API

    /// <summary>Builds a stack with the standard protocols and (when asked) the sample contribution.</summary>
    /// <param name="withSample">Whether the sample plugin contributes to the stack.</param>
    /// <returns>The built stack; the caller disposes it.</returns>
    internal static Stack Build(bool withSample = true)
    {
        SettingsManager settings = new();
        StackBuilder builder = new(settings, new FrameInterfaceRegistry());
        builder.RegisterStandardProtocols();
        if (withSample)
        {
            // The plugin declares its contribution through the builder, as it does for a real loader.
            RecordingPluginBuilder recording = new();
            new SampleProtocolPlugin().Configure(recording);
            foreach (Action<IStackBuilder> contribution in recording.StackContributions)
            {
                contribution(builder);
            }
        }

        return builder.Build();
    }

    /// <summary>Wraps raw bytes into an Ethernet frame of the stack's interface registry.</summary>
    /// <param name="stack">The stack whose registry creates the frame.</param>
    /// <param name="bytes">The frame bytes.</param>
    /// <param name="id">Frame id and second of the timestamp.</param>
    /// <returns>The frame.</returns>
    internal static Frame MakeFrame(Stack stack, byte[] bytes, int id = 0) =>
        NetworkInspector.Core.Frame.Create(id, Timestamp.FromSecs(id), bytes, LinkType.Ethernet,
            FrameInterfaceId.Invalid, stack.FrameInterfaceRegistry).Value;

    /// <summary>Parses a frame completely (field tree built) and returns the packet.</summary>
    /// <param name="stack">The stack that parses.</param>
    /// <param name="frame">The frame to parse.</param>
    /// <returns>The parsed packet.</returns>
    internal static Packet Parse(Stack stack, Frame frame)
    {
        ParseOptions options = new();
        if (!Packet.TryParse(frame.Id.Value, stack, frame, in options, out Packet? packet, out ParseFailure failure)
            || packet is null)
        {
            throw new InvalidOperationException(failure.ToString());
        }

        return packet;
    }

    /// <summary>Reads an unsigned field of a packet, or <see langword="null"/> when the field is absent.</summary>
    /// <param name="stack">The stack that knows the field name.</param>
    /// <param name="packet">The parsed packet.</param>
    /// <param name="fieldName">Registered field name, for example <c>sample.speed</c>.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    internal static ulong? U64(Stack stack, Packet packet, string fieldName)
    {
        FieldId? id = stack.GetFieldId(fieldName);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        return value.Data.TryGetAsU64(out ulong number)
            ? number
            : null;
    }

    /// <summary>Reads a field of a packet as text, or <see langword="null"/> when the field is absent.</summary>
    /// <param name="stack">The stack that knows the field name.</param>
    /// <param name="packet">The parsed packet.</param>
    /// <param name="fieldName">Registered field name, for example <c>packet.error</c>.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    internal static string? Text(Stack stack, Packet packet, string fieldName)
    {
        FieldId? id = stack.GetFieldId(fieldName);
        if (id is null || !packet.TryGetFieldValue(id.Value, out FieldValue value, materialize: true))
        {
            return null;
        }

        return value.ToString(null, CultureInfo.InvariantCulture);
    }

    #endregion
}
