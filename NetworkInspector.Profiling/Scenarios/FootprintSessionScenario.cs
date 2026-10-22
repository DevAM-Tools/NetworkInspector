// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Retained managed memory per packet of a session that ingested one memory-mapped PCAPNG source (500 000 IMIX
/// frames) or two sources of 250 000 IMIX frames each, read concurrently so their packets interleave. The value
/// includes the sources' index and the session's packet → frame map and packet index.
/// </summary>
/// <remarks><b>Thread safety.</b> Not thread-safe; the runner calls Setup, Run, and Cleanup sequentially.</remarks>
internal sealed class FootprintSessionScenario : FootprintScenarioBase
{
    #region Constants

    private const int _TotalPackets = 500_000;

    #endregion

    #region Fields

    private readonly bool _Interleaved;
    private Stack? _Stack;
    private string[] _Paths = [];

    #endregion

    #region Constructors

    /// <summary>Creates the scenario.</summary>
    /// <param name="interleaved">True for two sources of half the packets each; false for one source.</param>
    internal FootprintSessionScenario(bool interleaved) => _Interleaved = interleaved;

    #endregion

    #region IProfilingScenario

    /// <inheritdoc/>
    public override string Name => _Interleaved
        ? "footprint-session-pcapng-interleaved"
        : "footprint-session-pcapng";

    /// <inheritdoc/>
    public override string Description => _Interleaved
        ? Invariant($"Retained bytes per packet of a session over 2 × {_TotalPackets / 2:N0} IMIX frames (mapped PCAPNG).")
        : Invariant($"Retained bytes per packet of a session over {_TotalPackets:N0} IMIX frames (mapped PCAPNG).");

    /// <inheritdoc/>
    public override long WorkUnitsPerIteration => _TotalPackets;

    /// <inheritdoc/>
    public override string WorkUnitName => "packets";

    /// <inheritdoc/>
    public override void Setup()
    {
        _Stack = StackHelper.CreateStack();
        int sources = _Interleaved ? 2 : 1;
        int perSource = _TotalPackets / sources;
        _Paths = new string[sources];
        for (int s = 0; s < sources; s++)
        {
            _Paths[s] = CaptureWorkloads.CreateFile(CaptureWorkload.PcapngImix, perSource, _Stack, Invariant($"{Name}-{s}.pcapng"));
            CaptureWorkloads.Verify(Name, CaptureWorkload.PcapngImix, _Paths[s], perSource);
        }
    }

    /// <inheritdoc/>
    public override void Cleanup()
    {
        SampleCaptureFiles.Cleanup();
        _Stack?.Dispose();
        _Stack = null;
        _Paths = [];
    }

    #endregion

    #region Protected API

    /// <inheritdoc/>
    protected override IDisposable OpenAndScan()
    {
        // The session owns its sources: Dispose shuts it down and disposes them.
        Session session = new(_Stack!);
        foreach (string path in _Paths)
        {
            IRandomAccessFrameSource source = CaptureWorkloads.Open(CaptureWorkload.PcapngImix, path, blfCacheBudget: 0);
            if (!session.TryAddFrameSource(source, out _, out SessionFailure? addFailure))
            {
                source.Dispose();
                session.Dispose();
                throw new InvalidOperationException(Invariant($"{Name}: adding a source failed: {addFailure.Value.Message}"));
            }
        }

        if (!session.TryStart(out SessionFailure? startFailure))
        {
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: session start failed: {startFailure.Value.Message}"));
        }

        // A short ingest would report a too small denominator; fail instead of producing a number.
        _ = session.WaitForCompletion();
        if (session.PacketCount != _TotalPackets)
        {
            int packets = session.PacketCount;
            session.Dispose();
            throw new InvalidOperationException(Invariant($"{Name}: ingested {packets} packets, expected {_TotalPackets}."));
        }

        return session;
    }

    #endregion
}
