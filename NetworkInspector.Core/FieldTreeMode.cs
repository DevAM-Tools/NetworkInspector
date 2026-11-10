// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Controls whether a parse writes the field-tree slab.
/// An enum, not a <see langword="bool"/>, so a positional <c>false</c> cannot be misread as skip.
/// </summary>
public enum FieldTreeMode : byte
{
    /// <summary>
    /// Default. FieldBodies and tree links are written. The sealed packet is safe to retain, filter, export, and walk.
    /// </summary>
    Build = 0,

    /// <summary>
    /// Throwaway parse. No FieldBody slab. Packet index and ValueCache record still run.
    /// Do not retain, filter, export, or publish this packet to another thread.
    /// Skipping the slab saves the per-field storage and tree-link work for callers that only need the side effects of a
    /// parse (for example the packet index or value caches). Only the synthetic root is readable:
    /// <see cref="Fields.Field.Value"/> on any other field throws, and <see cref="Fields.Field.TryGetValue"/> returns
    /// <see langword="false"/>.
    /// </summary>
    Skip = 1,
}
