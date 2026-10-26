// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// Drives a FrameBuilder stack once and returns the frame as a new array, so that the recipes compose their frames with the
/// explicit <c>FrameStack.Start(…).Then(…).CreateWithFixedValues()</c> chain and keep the buffer handling in one place.
/// Stateless; thread-safe.
/// </summary>
internal static class FrameEmitting
{
    /// <summary>Emits the single frame of a stack that does not fragment.</summary>
    /// <typeparam name="TStack">The stack of layers.</typeparam>
    /// <typeparam name="TTrailer">The trailer type.</typeparam>
    /// <typeparam name="TInterceptor">The interceptor type.</typeparam>
    /// <param name="created">The stack with fixed values.</param>
    /// <param name="payload">Payload bytes behind the innermost header.</param>
    /// <returns>A new array of exactly the length of the frame.</returns>
    /// <exception cref="InvalidOperationException">The stack did not produce a frame (the status says why).</exception>
    internal static byte[] EmitFrame<TStack, TTrailer, TInterceptor>(
        this in CreatedStack<TStack, TTrailer, TInterceptor> created,
        ReadOnlySpan<byte> payload)
        where TStack : struct, IStackNode, IStatelessStack
        where TTrailer : struct, ITrailerLayer
        where TInterceptor : struct, IFrameInterceptor
    {
        // The buffer holds exactly one frame: headers, payload, and trailer. A stack whose payload exceeds the link limit does
        // not fragment here (the recipes set don't-fragment); the build then reports FragmentationRequired and MoveNext
        // returns false, which becomes the exception below instead of a silently truncated frame.
        byte[] buffer = new byte[created.HeaderSize + payload.Length + created.TrailerSize];
        using FrameSequence<TStack, TTrailer, TInterceptor> sequence = created.Build(payload);
        // Two failures end here: MoveNext returned false (the stack produced no frame), or it returned true with a status other
        // than success and nothing written. The parentheses state the grouping that && over || already gives.
        if (!sequence.MoveNext(buffer, out int written) || (sequence.Status != BuildStatus.Success && written == 0))
        {
            throw new InvalidOperationException($"The frame could not be built: {sequence.Status}.");
        }

        // Normally the frame fills the buffer and is returned without a copy; if the stack wrote less than it reserved,
        // copy to an array of the true length so that the caller never sees trailing bytes that are not part of the frame.
        return written == buffer.Length
            ? buffer
            : buffer.AsSpan(0, written).ToArray();
    }
}
