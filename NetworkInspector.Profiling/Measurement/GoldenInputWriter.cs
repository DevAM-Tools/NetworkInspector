// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Measurement;

/// <summary>
/// Writes deterministic reference captures for export comparisons: every <see cref="RandomFrameMode"/> × seeds 1 and
/// 2, <see cref="FramesPerInput"/> frames each, as <c>{mode}-{seed}.pcapng</c>.
/// <para>Determinism: fixed seeds and a fixed base timestamp (the source default would use the wall clock), so two
/// runs write byte-identical files.</para>
/// <para><b>Thread-safety:</b> not thread-safe; writes files sequentially.</para>
/// </summary>
internal static class GoldenInputWriter
{
    #region Constants

    /// <summary>Frames per reference capture.</summary>
    internal const int FramesPerInput = 5_000;

    /// <summary>Base timestamp of every capture: 2023-11-14T22:13:20Z.</summary>
    private const long _BaseSeconds = 1_700_000_000;

    private static readonly ulong[] _Seeds = [1UL, 2UL];

    #endregion

    #region Public API

    /// <summary>Writes all captures into <paramref name="directory"/>. Returns 0 on success, 1 when a frame was rejected.</summary>
    internal static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (RandomFrameMode mode in Enum.GetValues<RandomFrameMode>())
        {
            foreach (ulong seed in _Seeds)
            {
                string path = Path.Combine(directory, Invariant($"{mode.ToString().ToLowerInvariant()}-{seed}.pcapng"));
                if (!_WriteCapture(path, mode, seed))
                {
                    Console.Error.WriteLine(Invariant($"Exporter rejected a frame for {path}."));
                    return 1;
                }
            }
        }

        return 0;
    }

    #endregion

    #region Private helpers

    private static bool _WriteCapture(string path, RandomFrameMode mode, ulong seed)
    {
        // A private registry per capture keeps interface ids identical across runs and modes.
        FrameInterfaceRegistry registry = new();
        using RandomFrameSource source = new(new RandomSourceOptions
        {
            FrameCount = FramesPerInput,
            Seed = seed,
            Mode = mode,
            BaseTimestamp = Timestamp.FromSecs(_BaseSeconds),
            TcpStreamOptions = new TcpStreamOptions { StreamCount = 16, SegmentsPerStream = 40 },
        });
        FrameSourceId sourceId = registry.RegisterSource(source);
        source.Start(sourceId, registry);

        using Exporters.Pcapng.PcapngExporter exporter = Exporters.Pcapng.PcapngExporter.CreateBuilder().ToFile(path).Build();
        Frame? next;
        while ((next = source.NextFrame()) is not null)
        {
            if (!exporter.OnFrame(next.Value))
            {
                return false;
            }
        }

        exporter.OnFinish();
        return true;
    }

    #endregion
}
