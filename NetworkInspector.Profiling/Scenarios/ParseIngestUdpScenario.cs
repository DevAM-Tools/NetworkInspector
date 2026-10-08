// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// First parse of each packet id — mirrors the ordered parse path of the session source loop,
/// including the replay state the stateful protocols record while doing it.
/// </summary>
internal sealed class ParseIngestUdpScenario : IProfilingScenario
{
    private const int _BatchSize = 10_000;

    private Stack? _Stack;
    private Frame[]? _Frames;
    private int _PacketCounter;

    /// <inheritdoc/>
    public string Name => "parse-ingest-udp";

    /// <inheritdoc/>
    public string Description =>
        FormattableString.Invariant(
            $"TryParse first parse with protocol-recorded replay state, {_BatchSize:N0} IPv6/UDP frames per iteration.");

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => _BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        _Frames = FrameHelper.CreateSharedFrames(_BatchSize, _Stack);
        _PacketCounter = 0;
    }

    /// <inheritdoc/>
    public void Run()
    {
        Stack stack = _Stack!;
        Frame[] frames = _Frames!;
        int counter = _PacketCounter;

        // Default build options for the whole batch. The loop only supplies id and frame.
        ParseOptions options = new();
        for (int i = 0; i < _BatchSize; i++)
        {
            PacketId id = new(counter + i);
            if (!Packet.TryParse(id, stack, frames[i], in options, out _, out ParseFailure failure))
            {
                throw new InvalidOperationException(failure.ToString());
            }
        }

        _PacketCounter = counter + _BatchSize;
    }

    /// <inheritdoc/>
    public void Cleanup()
    {
        _Stack?.Dispose();
        _Stack = null;
        _Frames = null;
        _PacketCounter = 0;
    }
}
