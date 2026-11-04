// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Interfaces;

/// <summary>
/// A consumer that states its parse needs before packets flow. A pipeline asks every consumer, combines the demands with
/// <see cref="ParsePlan.TryCreate"/>, and parses each packet once with the plan's options.
/// <para>
/// A packet listener without this interface demands <see cref="ParseDemand.FieldTree"/> (it may walk the tree); a frame
/// listener demands <see cref="ParseDemand.FramesOnly"/>. <b>Why an extra interface instead of a member on
/// <see cref="IPacketListener"/>:</b> the listener stays the commit and packet-data channel, existing listeners keep working,
/// and non-exporters (filters, statistics) can state demands too.
/// </para>
/// </summary>
public interface IParseDemandSource
{
    /// <summary>The demand; stable for the lifetime of the consumer (a pipeline reads it once per run or output).</summary>
    ParseDemand ParseDemand { get; }
}
