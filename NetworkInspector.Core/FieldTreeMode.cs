// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core;

/// <summary>
/// Controls whether a parse writes the field-tree slab.
/// An enum, not a <see langword="bool"/>, so a positional <c>false</c> cannot be misread as skip.
/// </summary>
public enum FieldTreeMode : byte
{
    #region Public API

    /// <summary>
    /// Default. FieldBodies and tree links are written. The sealed packet is safe to retain, filter, export, and walk.
    /// </summary>
    Build = 0,

    /// <summary>
    /// Throwaway parse. No FieldBody slab. Packet index and ValueCache record still run.
    /// Do not retain, filter, export, or publish this packet to another thread.
    /// </summary>
    Skip = 1,

    #endregion
}
