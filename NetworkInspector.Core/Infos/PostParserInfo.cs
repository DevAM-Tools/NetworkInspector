// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered post-parser.</summary>
public sealed class PostParserInfo(
    PostParserId id,
    int priority,
    ProtocolId protocolId,
    string? description)
{
    #region Properties

    /// <summary>Unique post-parser identifier.</summary>
    public PostParserId Id { get; } = id;

    /// <summary>Execution priority (higher runs first, default 0).</summary>
    public int Priority { get; } = priority;

    /// <summary>Protocol that owns this post-parser.</summary>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <summary>Optional description text.</summary>
    public string? Description { get; } = description;

    #endregion
}
