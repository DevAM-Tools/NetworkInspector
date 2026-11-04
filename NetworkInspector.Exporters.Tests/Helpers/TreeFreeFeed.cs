// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Helpers;

/// <summary>
/// Drives an exporter the way the parse pipeline of the guide does: one <see cref="ParsePlan"/> from the exporter's
/// <see cref="IParseDemandSource.ParseDemand"/>, every frame parsed once with the plan's options, and the finished packet handed
/// to <see cref="IPacketListener.OnPacket"/>. The test-side counterpart of the CLI and session loops.
/// </summary>
internal static class TreeFreeFeed
{
    /// <summary>Parses every frame with the plan of <paramref name="exporter"/> and offers each packet to it.</summary>
    /// <typeparam name="TExporter">An exporter that states its parse need.</typeparam>
    /// <param name="exporter">The exporter; <see cref="IPacketListener.OnFinish"/> is not called.</param>
    /// <param name="frames">The frames to parse, in order.</param>
    /// <param name="offer">Decides per packet whether the exporter is offered it (the stand-in for a filter); null offers all.</param>
    /// <returns>For each frame, whether the parsed packet carried a field tree (it must not for a flat shape).</returns>
    internal static List<bool> Run<TExporter>(TExporter exporter, IReadOnlyList<Frame> frames, Func<Packet, bool>? offer = null)
        where TExporter : IPacketListener, IParseDemandSource
    {
        ParseOptions options = CreateOptions(exporter.ParseDemand);
        return Run(exporter, frames, in options, offer);
    }

    /// <summary>The same loop with ready-made parse options (for a test that wraps the exporter's observer).</summary>
    /// <typeparam name="TExporter">An exporter that offers packets.</typeparam>
    /// <param name="exporter">The exporter; <see cref="IPacketListener.OnFinish"/> is not called.</param>
    /// <param name="frames">The frames to parse, in order.</param>
    /// <param name="options">The options every frame is parsed with.</param>
    /// <param name="offer">Decides per packet whether the exporter is offered it; null offers all.</param>
    /// <returns>For each frame, whether the parsed packet carried a field tree.</returns>
    internal static List<bool> Run<TExporter>(
        TExporter exporter, IReadOnlyList<Frame> frames, in ParseOptions options, Func<Packet, bool>? offer = null)
        where TExporter : IPacketListener
    {
        List<bool> hadTree = new(frames.Count);
        Packet? recycle = null;
        foreach (Frame frame in frames)
        {
            Packet packet = TestHarness.ParseFrame(frame, in options, recycle);
            recycle = packet;
            hadTree.Add(packet.HasFieldTree);
            if (offer is null || offer(packet))
            {
                _ = exporter.OnPacket(packet);
            }
        }

        return hadTree;
    }

    /// <summary>Builds the parse options of one demand; throws when the plan is refused.</summary>
    /// <param name="demand">The demand of the exporter.</param>
    /// <returns>The options of the plan.</returns>
    internal static ParseOptions CreateOptions(ParseDemand demand)
    {
        ParseDemand[] demands = [demand];
        if (!ParsePlan.TryCreate(TestHarness.GetStack(), demands, out ParsePlan? plan, out ParsePlanError error))
        {
            throw new InvalidOperationException(error.Message);
        }

        return plan.Options;
    }
}
