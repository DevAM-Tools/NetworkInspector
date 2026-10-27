// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sessions.Tests.Helpers;

/// <summary>
/// A field observer that records what a parse reported: how often a packet began and which fields it visited. Used by the
/// parse-with-options tests and the guide sample. Thread-safety: not thread-safe; one observer instance serves one parse at a
/// time, as the contract of <see cref="IFieldObserver"/> demands.
/// </summary>
internal sealed class RecordingFieldObserver : IFieldObserver
{
    /// <summary>How many packets began.</summary>
    internal int BeganPacket { get; private set; }

    /// <summary>The ids of the visited fields, in visiting order.</summary>
    internal List<FieldId> Fields { get; } = [];

    /// <inheritdoc/>
    public void BeginPacket(PacketId id, Timestamp timestamp) => BeganPacket++;

    /// <inheritdoc/>
    public void OnField(in FieldVisit visit) => Fields.Add(visit.FieldId);
}
