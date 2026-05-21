// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Blf;

/// <summary>
/// Compression level presets for BLF container output.
/// Maps to zlib compression levels used internally.
/// </summary>
public enum BlfCompressionLevel
{
    /// <summary>No compression — objects stored raw.</summary>
    None = 0,

    /// <summary>Fastest compression with least CPU usage.</summary>
    Fast = 1,

    /// <summary>Default balanced compression (zlib level 6).</summary>
    Default = 6,

    /// <summary>Best compression ratio at the cost of higher CPU usage.</summary>
    Best = 9,
}
