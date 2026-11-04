// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>Field observer that records the watched fields and their unsigned values. Single-threaded.</summary>
internal sealed class RecordingFieldObserver : IFieldObserver
{
    #region Public API

    /// <summary>Reported field ids in order.</summary>
    public List<FieldId> Ids { get; } = [];

    /// <summary>Unsigned values parallel to <see cref="Ids"/> (0 for a value that is not unsigned).</summary>
    public List<ulong> Values { get; } = [];

    /// <inheritdoc/>
    public void BeginPacket(PacketId id, Timestamp timestamp)
    {
    }

    /// <inheritdoc/>
    public void OnField(in FieldVisit visit)
    {
        Ids.Add(visit.FieldId);
        Values.Add(visit.Value.Data.TryGetAsU64(out ulong number) ? number : 0);
    }

    /// <inheritdoc/>
    public void EndPacket(PacketId id, bool complete)
    {
    }

    #endregion
}
