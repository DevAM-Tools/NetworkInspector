// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Loader;

/// <summary>
/// Kinds of recorded contributions. The core tier records <see cref="Protocol"/>, <see cref="Heuristic"/>,
/// <see cref="PostParser"/>, <see cref="Reassembly"/>, <see cref="Setting"/>, <see cref="Source"/>, and
/// <see cref="Exporter"/>. The other kinds belong to higher tiers and later features; their owners record them through
/// <see cref="ContributionRecorder.Record"/>. The enumeration is complete from the start so that this assembly never changes
/// when a tier adds a kind.
/// </summary>
public enum ContributionKind : byte
{
    /// <summary>A protocol registered in the stack.</summary>
    Protocol,

    /// <summary>A heuristic parser registered in a heuristic table.</summary>
    Heuristic,

    /// <summary>A post-parser.</summary>
    PostParser,

    /// <summary>A stream reassembly configuration.</summary>
    Reassembly,

    /// <summary>A plugin setting or a protocol setting.</summary>
    Setting,

    /// <summary>A source kind.</summary>
    Source,

    /// <summary>An export format.</summary>
    Exporter,

    /// <summary>A table of value texts for a field.</summary>
    ValueTextTable,

    /// <summary>A command (host tier).</summary>
    Command,

    /// <summary>A widget (app tier).</summary>
    Widget,

    /// <summary>A popup (app tier).</summary>
    Popup,

    /// <summary>An action (app tier).</summary>
    Action,

    /// <summary>A menu entry (app tier).</summary>
    MenuEntry,

    /// <summary>A page (app tier).</summary>
    Page,

    /// <summary>A set of translated texts (host and app tier).</summary>
    Texts,

    /// <summary>A provider of exit impact lines (app tier).</summary>
    ExitImpact,
}
