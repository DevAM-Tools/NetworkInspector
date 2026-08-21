// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Helpers;

/// <summary>
/// A protocol that registers a field but no container field carrying its own name.
/// It is never dispatched; it exists so tests can bind names that resolve to a protocol without
/// a container field, which is the only way to reach the evaluator's owner-scan fallback.
/// </summary>
internal sealed class ContainerlessProtocol : IProtocol
{
    #region Identity

    /// <inheritdoc />
    public string Name => "noctr";

    /// <inheritdoc />
    public string UiName => "No Container";

    #endregion

    #region Parsing

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;

    #endregion
}
