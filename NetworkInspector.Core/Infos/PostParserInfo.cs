// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered post-parser.
/// <para>
/// Post-parsers run after the main protocol dispatch, before <c>packet.info</c> is appended and
/// before the packet is sealed. They are sorted deterministically at build time: ascending by
/// <see cref="Priority"/> first, then ascending by <see cref="Id"/> (registration order) as a
/// stable tie-breaker. A lower <see cref="Priority"/> value therefore runs earlier.
/// </para>
/// <para>
/// Error policy: a <see cref="ParseResult"/> error or an exception thrown by a post-parser is
/// recorded as a packet-level error and does not suppress the remaining post-parsers.
/// </para>
/// <para>
/// Concurrency: post-parsers execute within the single-writer parse path. The same
/// thread-safety contract as for normal parsers applies — no additional locking is introduced.
/// </para>
/// </summary>
public sealed class PostParserInfo(
    PostParserId id,
    int priority,
    ProtocolId protocolId,
    string? description)
{
    #region Properties

    /// <summary>Unique post-parser identifier. Reflects registration order; used as tie-breaker in build-time sort.</summary>
    public PostParserId Id { get; } = id;

    /// <summary>Execution priority. Lower values run first; default is 0. Equal-priority post-parsers run in registration order.</summary>
    public int Priority { get; } = priority;

    /// <summary>Protocol that owns this post-parser.</summary>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    #endregion
}
