// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Pcapng.Format.Blocks;

/// <summary>
/// PCAPNG option header — 4 bytes.
/// Each option within a block body starts with this TLV header.
/// The value that follows is padded to a 4-byte boundary.
/// </summary>
[BinaryParsable]
internal readonly partial struct OptionHeader
{
    /// <summary>Option code. 0 = end-of-options, 1 = comment, others are block-specific.</summary>
    public U16LE Code
    {
        get; init;
    }

    /// <summary>Length of the option value in bytes (not including padding).</summary>
    public U16LE Length
    {
        get; init;
    }
}
