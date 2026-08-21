// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>
/// End-to-end coverage for the <c>--filter</c> option of <see cref="ConvertCommand"/> and
/// <see cref="ExportCommand"/>, plus the shared <see cref="CliFilter"/> helpers.
/// </summary>
internal sealed class FilterOptionTests
{
    #region CliFilter

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task IsActive_BlankExpression_IsFalse(string? expression)
        => await Assert.That(CliFilter.IsActive(expression)).IsFalse();

    [Test]
    public async Task IsActive_RealExpression_IsTrue()
        => await Assert.That(CliFilter.IsActive("udp")).IsTrue();

    [Test]
    public async Task TryCompile_ValidExpression_ProducesFilter()
    {
        using Stack stack = _BuildStack();

        bool compiled = CliFilter.TryCompile("udp", stack, out IFilter? filter);

        await Assert.That(compiled).IsTrue();
        await Assert.That(filter!.Expression).IsEqualTo("udp");
    }

    [Test]
    public async Task TryCompile_UnknownField_Fails()
    {
        using Stack stack = _BuildStack();

        bool compiled = CliFilter.TryCompile("nosuch.field == 1", stack, out IFilter? filter);

        await Assert.That(compiled).IsFalse();
        await Assert.That(filter).IsNull();
    }

    [Test]
    public async Task TryMatch_MatchingPacket_ReportsMatch()
    {
        using Stack stack = _BuildStack();
        PacketIndex index = new(stack);
        Packet packet = _ParseOneIndexedFrame(stack, index, packetId: 1);
        _ = CliFilter.TryCompile("udp", stack, out IFilter? filter);

        bool decided = CliFilter.TryMatch(filter!, packet, index, out bool matched);

        await Assert.That(decided).IsTrue();
        await Assert.That(matched).IsTrue();
    }

    [Test]
    public async Task TryMatch_PoisonedFilter_ReportsFailure()
    {
        using Stack stack = _BuildStack();
        PacketIndex index = new(stack);
        _ = CliFilter.TryCompile("flank(ip.ttl, changed, within: 1s)", stack, out IFilter? filter);

        // Evaluating a high packet id first makes the replay of a lower id an out-of-order error.
        _ = filter!.TryIsMatch(_ParseOneIndexedFrame(stack, index, packetId: 500), index, out _, out _);
        bool decided = CliFilter.TryMatch(filter, _ParseOneIndexedFrame(stack, index, packetId: 1), index, out bool matched);

        await Assert.That(decided).IsFalse();
        await Assert.That(matched).IsFalse();
    }

    #endregion

    #region Convert

    [Test]
    public async Task Convert_FilterMatchingEverything_WritesOutput()
    {
        string path = _TempPath("pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=5,mode=udp4",
                "-o", path,
                "--filter", "udp",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Convert_FilterMatchingNothing_WritesNoOutput()
    {
        string path = _TempPath("pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=5,mode=udp4",
                "-o", path,
                "--filter", "tcp",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Convert_BlankFilter_StaysOnTheFramePath()
    {
        string path = _TempPath("pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=5,mode=udp4",
                "-o", path,
                "--filter", "   ",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Convert_InvalidFilter_ReturnsArgumentError()
    {
        string path = _TempPath("pcapng");
        try
        {
            int code = ConvertCommand.Run([
                "random:count=5,mode=udp4",
                "-o", path,
                "--filter", "nosuch.field == 1",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Convert_MissingFilterValue_ReturnsArgumentError()
    {
        int code = ConvertCommand.Run(["random:count=1,mode=udp4", "-o", "out.pcapng", "--filter"]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    #endregion

    #region Export

    [Test]
    public async Task Export_FilterMatchingEverything_WritesOutput()
    {
        string path = _TempPath("json");
        try
        {
            int code = ExportCommand.Run([
                "random:count=5,mode=udp4",
                "-f", "json:style=compact",
                "-o", path,
                "--filter", "udp",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsTrue();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Export_FilterMatchingNothing_WritesNoOutput()
    {
        string path = _TempPath("json");
        try
        {
            int code = ExportCommand.Run([
                "random:count=5,mode=udp4",
                "-f", "json:style=compact",
                "-o", path,
                "--filter", "tcp",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.Success);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Export_InvalidFilter_ReturnsArgumentError()
    {
        string path = _TempPath("json");
        try
        {
            int code = ExportCommand.Run([
                "random:count=5,mode=udp4",
                "-f", "json:style=compact",
                "-o", path,
                "--filter", "udp.dstport ==",
            ]);

            await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            _Delete(path);
        }
    }

    [Test]
    public async Task Export_MissingFilterValue_ReturnsArgumentError()
    {
        int code = ExportCommand.Run([
            "random:count=1,mode=udp4",
            "-f", "json",
            "-o", "out.json",
            "--filter",
        ]);

        await Assert.That(code).IsEqualTo((int)ExitCode.ArgumentError);
    }

    #endregion

    #region Helpers

    /// <summary>Builds a stack with all standard protocols; the caller disposes it.</summary>
    private static Stack _BuildStack()
    {
        using SettingsManager settingsManager = new();
        StackBuilder stackBuilder = new(settingsManager, new FrameInterfaceRegistry());
        stackBuilder.RegisterStandardProtocols();
        return stackBuilder.Build();
    }

    /// <summary>Parses a single synthetic UDP/IPv4 frame on the given stack.</summary>
    private static Packet _ParseOneRandomFrame(Stack stack, int packetId)
    {
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        using RandomFrameSource source = new(count: 1, seed: 7, mode: RandomFrameMode.UdpIPv4);
        source.Start(registry.RegisterSource(source), registry);
        Frame frame = source.NextFrame()!.Value;
        return Packet.ParseFrame(new PacketId(packetId), stack, frame);
    }

    /// <summary>Parses a frame into the live <paramref name="index"/> used by CLI filter eval.</summary>
    private static Packet _ParseOneIndexedFrame(Stack stack, PacketIndex index, int packetId)
    {
        FrameInterfaceRegistry registry = stack.FrameInterfaceRegistry;
        using RandomFrameSource source = new(count: 1, seed: 7, mode: RandomFrameMode.UdpIPv4);
        source.Start(registry.RegisterSource(source), registry);
        Frame frame = source.NextFrame()!.Value;
        return Packet.ParseFrameIndexed(new PacketId(packetId), stack, frame, index);
    }

    /// <summary>A unique path in the temp directory with the given extension.</summary>
    private static string _TempPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"ni-filter-{Guid.NewGuid():N}.{extension}");

    /// <summary>Removes a temporary output file if the command created one.</summary>
    private static void _Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    #endregion
}
