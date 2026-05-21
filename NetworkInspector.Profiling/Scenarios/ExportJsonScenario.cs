// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Profiling.Scenarios;

/// <summary>
/// Profiling scenario that exports pre-parsed IPv6/UDP packets as JSON (Compact)
/// into a <see cref="MemoryStream"/> on every <see cref="Run"/> call.
///
/// <para>
/// <b>Hot path:</b> <see cref="Exporters.Json.JsonExporter.OnPacket"/> serialisation,
/// including field enumeration and SIMD escape scanning.
/// Frame generation and parsing happen in <see cref="Setup"/>.
/// </para>
/// </summary>
internal sealed class ExportJsonScenario : IProfilingScenario, IDisposable
{
    /// <summary>Number of packets exported per <see cref="Run"/> call.</summary>
    private const int BatchSize = 10_000;

    private Stack? _Stack;
    private Packet[]? _Packets;
    private MemoryStream? _Stream;

    /// <inheritdoc/>
    public string Name => "export-json";

    /// <inheritdoc/>
    public string Description =>
        $"Export {BatchSize:N0} parsed IPv6/UDP packets as JSON (Compact) → MemoryStream per iteration.";

    /// <inheritdoc/>
    public long WorkUnitsPerIteration => BatchSize;

    /// <inheritdoc/>
    public string WorkUnitName => "packets";

    /// <inheritdoc/>
    public void Setup()
    {
        _Stack = StackHelper.CreateStack();
        Frame[] frames = FrameHelper.CreateSharedFrames(BatchSize, _Stack);
        _Packets = FrameHelper.ParseAndMaterialize(frames, _Stack);
        _Stream = new MemoryStream(8 * 1024 * 1024); // 8 MiB initial capacity
    }

    /// <inheritdoc/>
    public void Run()
    {
        MemoryStream ms = _Stream!;
        ms.SetLength(0);

        using Exporters.Json.JsonExporter exporter = Exporters.Json.JsonExporter
            .CreateBuilder()
            .ToStream(ms)
            .WithFormat(Exporters.Json.JsonExportFormat.Compact)
            .Build();

        foreach (Packet packet in _Packets!)
        {
            if (!exporter.OnPacket(packet))
            {
                break;
            }
        }

        exporter.OnFinish();
    }

    /// <inheritdoc/>
    public void Cleanup() => Dispose();

    /// <inheritdoc/>
    public void Dispose()
    {
        _Stream?.Dispose();
        _Stream = null;
        _Stack?.Dispose();
        _Stack = null;
        _Packets = null;
    }
}
