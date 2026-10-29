// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Proves that the lazy populators of a protocol are safe to run on several threads: every frame is parsed once on the
/// calling thread and materialised into a snapshot, then many tasks parse the same frames again on the same stack and
/// compare their materialised fields to the snapshot.
/// <para>
/// <b>Why this shape:</b> a protocol instance is shared by every packet of the stack, so its populators must read
/// only immutable state. Parsing and materialising on several threads at once is the cheapest way to make a hidden
/// shared write (a scratch buffer, a cache) visible as a differing field list. Each task owns its packet, because
/// materialising one packet from two threads is not part of the contract.
/// </para>
/// <para>Thread safety: static and stateless.</para>
/// </summary>
internal static class ConcurrentMaterialization
{
    /// <summary>Number of parallel parses per frame; large enough to overlap on a multi-core machine, small enough to stay well below 100 ms.</summary>
    private const int _TasksPerFrame = 8;

    /// <summary>
    /// Parses every frame once as the reference, then parses each frame <see cref="_TasksPerFrame"/> more times on
    /// concurrent tasks and asserts that the materialised fields equal the reference.
    /// </summary>
    /// <param name="frames">Ethernet frames to parse; each is compared with its own snapshot.</param>
    internal static async Task AssertMatchesSingleThreadedResult(params byte[][] frames)
    {
        using Stack stack = ProtocolTestHelper.BuildStack();

        // Reference snapshots, taken on this thread before any concurrency starts.
        PacketFieldComparer.PacketFieldSnapshot[] snapshots = new PacketFieldComparer.PacketFieldSnapshot[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            Packet reference = ProtocolTestHelper.ParseFrame(stack, frames[i], i, Timestamp.FromMillis(i));
            snapshots[i] = PacketFieldComparer.CaptureFields(reference);
        }

        List<Task> tasks = new(frames.Length * _TasksPerFrame);
        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            for (int repeat = 0; repeat < _TasksPerFrame; repeat++)
            {
                int capturedFrame = frameIndex;
                // A repeat parses the same packet id again (a replay): first parses of new ids must be sequential on one
                // stack, replays may run concurrently, which is the contract the lazy populators are exercised under.
                tasks.Add(Task.Run(async () =>
                {
                    Packet packet = ProtocolTestHelper.ParseFrame(stack, frames[capturedFrame], capturedFrame, Timestamp.FromMillis(capturedFrame));
                    await PacketFieldComparer.AssertMatchesSnapshot(stack, snapshots[capturedFrame], packet).ConfigureAwait(false);
                }));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
