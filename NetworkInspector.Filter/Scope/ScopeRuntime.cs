// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Scope;

/// <summary>
/// The compiled form of a <c>$Name[i?] { … }</c> scope.
/// <para>
/// The anchor is resolved once at compile time into either a set of field ids or a protocol id.
/// At runtime the evaluator performs a breadth-first search of the active domain for anchor hits
/// and evaluates <see cref="Body"/> with the hit's subtree as the new domain.
/// </para>
/// <para>
/// Without an occurrence index the scope is existential: it matches when <b>any</b> hit satisfies
/// the body. With <c>[i]</c> it selects the <c>i</c>-th breadth-first hit and evaluates the body
/// only there; an index past the last hit yields <see langword="false"/> instead of an error,
/// so <c>$udp[1] { … }</c> is a normal non-match on single-UDP packets.
/// </para>
/// </summary>
internal sealed class ScopeRuntime
{
    #region Construction

    /// <summary>Creates a scope runtime from a resolved anchor and a compiled body.</summary>
    public ScopeRuntime(
        string name,
        FieldId[] anchorFields,
        ProtocolId anchorProtocol,
        int? occurrence,
        FilterEvalFn body)
    {
        Name = name;
        AnchorFields = anchorFields;
        AnchorProtocol = anchorProtocol;
        Occurrence = occurrence;
        Body = body;
    }

    #endregion

    #region Properties

    /// <summary>The anchor name as written.</summary>
    public string Name { get; }

    /// <summary>Field ids a node must carry to be a hit; empty for a protocol anchor.</summary>
    public FieldId[] AnchorFields { get; }

    /// <summary>Protocol used for presence-index short-circuit, or <see cref="ProtocolId.Invalid"/>.</summary>
    /// <remarks>
    /// When the anchor matches on a container <see cref="AnchorFields"/> entry, this is still the
    /// owning protocol id so <c>FindAnchors</c> can skip the BFS when the index proves absence.
    /// </remarks>
    public ProtocolId AnchorProtocol { get; }

    /// <summary>Selected breadth-first hit, or <see langword="null"/> for the existential form.</summary>
    public int? Occurrence { get; }

    /// <summary>The compiled body, evaluated with the anchor hit's subtree as the domain.</summary>
    public FilterEvalFn Body { get; }

    #endregion
}
