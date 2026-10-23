// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Heuristic parser that recognizes a sample payload in TCP data on any port. TCP consults its heuristic table only when port
/// dispatch did not consume the payload, so protocols with a port registration keep priority. Stateless and thread-safe.
/// </summary>
/// <param name="protocolId">The sample protocol this parser identifies.</param>
internal sealed class SampleHeuristicParser(ProtocolId protocolId) : IHeuristicParser
{
    #region IHeuristicParser

    /// <inheritdoc/>
    public ProtocolId ProtocolId { get; } = protocolId;

    /// <inheritdoc/>
    public string Name => "sample.heuristic";

    /// <inheritdoc/>
    public string UiName => "Sample NIS1 magic";

    /// <inheritdoc/>
    public string? Description => "Detects the NIS1 magic in TCP payload on any port.";

    /// <inheritdoc/>
    public bool Test(ReadOnlyMemory<byte> data)
    {
        // A complete payload is required: a shorter segment that starts with the magic is not a sample payload yet.
        return data.Length >= SampleWire.PayloadLength
            && data.Span[..SampleWire.CounterOffset].SequenceEqual(SampleWire.MagicBytes);
    }

    #endregion
}
