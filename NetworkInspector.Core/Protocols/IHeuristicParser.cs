// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Contract for heuristic protocol parsers that identify protocols by inspecting data.
/// </summary>
public interface IHeuristicParser
{
    /// <summary>
    /// Tests whether this parser can handle the given data.
    /// <para>
    /// The heuristic table calls <c>Test</c> on its parsers in registration order and stops at the first parser that
    /// returns <see langword="true"/>, so registration order decides between overlapping heuristics, and a parser behind
    /// a match is never called. The result is a plain yes/no without error details, and a rejected
    /// parser is simply skipped, so implementations should be cheap and free of side effects.
    /// </para>
    /// </summary>
    /// <param name="data">The candidate payload bytes to inspect. Must not be stored.</param>
    /// <returns><see langword="true"/> when the data looks like this parser's protocol.</returns>
    bool Test(ReadOnlyMemory<byte> data);

    /// <summary>The protocol this parser identifies.</summary>
    ProtocolId ProtocolId
    {
        get;
    }

    /// <summary>Machine-readable parser name.</summary>
    string Name
    {
        get;
    }

    /// <summary>Human-readable display name.</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional description.</summary>
    string? Description => null;
}
