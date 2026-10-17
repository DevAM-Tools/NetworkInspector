// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Stress;

/// <summary>
/// A session shutdown with a timeout disposes sources whose read loop may still be inside <c>NextFrame</c>. On the mapped
/// backend the sequential path reads the view through a raw pointer, so an unmap under a running copy would be an access
/// violation (a process crash, not an exception). These tests dispose mapped sources from another thread while
/// <c>NextFrame</c> runs; the reader may only see frames, <c>null</c>, or <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class DisposeDuringReadStressTests
{
    #region Constants

    private const int _Rounds = 24;
    private const int _FrameCount = 4_000;

    private static readonly byte[] _SrcMac = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55];
    private static readonly byte[] _DstMac = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    #endregion

    #region Tests

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task PcapngMappedDisposeDuringNextFrameIsSafe(ScanMode scanMode)
    {
        using TempCaptureFile file = new(_BuildPcapng(), ".pcapng");
        PcapngSourceOptions options = new() { PreloadBudget = null, ScanMode = scanMode, FrameSlabSize = 4096 };

        List<Exception> unexpected = [];
        for (int round = 0; round < _Rounds; round++)
        {
            Exception? failure = await _DisposeWhileReading(PcapngSource.Open(file.Path, options));
            if (failure is not null)
            {
                unexpected.Add(failure);
            }
        }

        await Assert.That(unexpected).IsEmpty();
    }

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task BlfMappedDisposeDuringNextFrameIsSafe(ScanMode scanMode)
    {
        using TempCaptureFile file = new(_BuildBlf(), ".blf");
        BlfSourceOptions options = new() { PreloadBudget = null, ScanMode = scanMode };

        List<Exception> unexpected = [];
        for (int round = 0; round < _Rounds; round++)
        {
            Exception? failure = await _DisposeWhileReading(BlfSource.Open(file.Path, options));
            if (failure is not null)
            {
                unexpected.Add(failure);
            }
        }

        await Assert.That(unexpected).IsEmpty();
    }

    [Test]
    [Arguments(ScanMode.Lazy)]
    [Arguments(ScanMode.Full)]
    public async Task PcapngMappedDisposeInsideNextFrameKeepsTheViewMapped(ScanMode scanMode)
    {
        // Deterministic variant: the FrameSkipped handler runs inside NextFrame and disposes the source there; NextFrame
        // then keeps reading the view (the next block in lazy mode, the frame copy in full mode). Without a lease held
        // for the whole call the view is unmapped at that point.
        using TempCaptureFile file = new(_BuildPcapngWithBadBlock(), ".pcapng");
        using PcapngSource source = PcapngSource.Open(
            file.Path, new PcapngSourceOptions { PreloadBudget = null, ScanMode = scanMode });

        Exception? failure = _DisposeFromSkipHandler(source);

        await Assert.That(failure).IsNull();
        await Assert.That(source.IsRunning).IsFalse();
    }

    [Test]
    public async Task BlfMappedDisposeInsideNextFrameKeepsTheViewMapped()
    {
        using TempCaptureFile file = new(_BuildBlfWithBadObject(), ".blf");
        using BlfSource source = BlfSource.Open(file.Path, new BlfSourceOptions { PreloadBudget = null, ScanMode = ScanMode.Lazy });

        Exception? failure = _DisposeFromSkipHandler(source);

        await Assert.That(failure).IsNull();
        await Assert.That(source.IsRunning).IsFalse();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reads <paramref name="source"/> to the end on this thread; the first <c>FrameSkipped</c> disposes it from inside
    /// <c>NextFrame</c>. Returns any exception except <see cref="ObjectDisposedException"/>, or null.
    /// </summary>
    private static Exception? _DisposeFromSkipHandler(IErrorTolerantFrameSource source)
    {
        source.FrameSkipped += (_, _) => source.Dispose();
        SourceTestFixture.InitializeAndStartSource(source);
        try
        {
            while (source.NextFrame() is not null)
            {
            }
        }
        catch (ObjectDisposedException)
        {
            // The call after the one that disposed.
        }
        catch (Exception ex)
        {
            return ex;
        }

        return null;
    }

    /// <summary>Ten good frames, an EPB on an undeclared interface, then many good frames.</summary>
    private static byte[] _BuildPcapngWithBadBlock()
    {
        using PcapNgTestWriter writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < _FrameCount; i++)
        {
            uint interfaceId = i == 10 ? 7u : 0u;
            writer.WriteFrame(interfaceId, (i + 1) * 1_000L, FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i]));
        }

        return writer.Build();
    }

    /// <summary>Ten good CAN objects, one CAN object too short to parse, then many good ones (all top level).</summary>
    private static byte[] _BuildBlfWithBadObject()
    {
        BlfTestGenerator gen = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            if (i == 10)
            {
                _ = gen.AddRawObject(BlfConstants.ObjTypeCanMessage, (i + 1) * 1_000L, [1, 0, 0, 8]);
                continue;
            }

            _ = gen.AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x100u + (uint)(i & 0xFF), [(byte)i]), (i + 1) * 1_000L);
        }

        return gen.Build();
    }

    /// <summary>
    /// Starts a reader thread on <paramref name="source"/>, disposes the source as soon as the first frame arrived, and
    /// returns the reader's unexpected exception (anything but <see cref="ObjectDisposedException"/>), or null.
    /// </summary>
    private static async Task<Exception?> _DisposeWhileReading(IFrameSource source)
    {
        SourceTestFixture.InitializeAndStartSource(source);
        using ManualResetEventSlim firstFrame = new(false);

        Task<Exception?> reader = Task.Run(() =>
        {
            try
            {
                while (source.NextFrame() is not null)
                {
                    firstFrame.Set();
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected when Dispose won the race before this call started.
            }
            catch (Exception ex)
            {
                return ex;
            }
            finally
            {
                firstFrame.Set();
            }

            return (Exception?)null;
        });

        // Dispose while the reader is in the middle of the file (the event fires on the first frame, long before the end).
        firstFrame.Wait();
        source.Dispose();
        return await reader.ConfigureAwait(false);
    }

    private static byte[] _BuildPcapng()
    {
        using PcapNgTestWriter writer = new();
        _ = writer.AddInterface(LinkType.Ethernet, nanosecondResolution: true);
        for (int i = 0; i < _FrameCount; i++)
        {
            writer.WriteFrame(0, (i + 1) * 1_000L, FrameBuilders.BuildEthernetFrame(_DstMac, _SrcMac, 0x0800, [(byte)i, (byte)(i >> 8)]));
        }

        return writer.Build();
    }

    private static byte[] _BuildBlf()
    {
        BlfTestGenerator inner = new();
        for (int i = 0; i < _FrameCount; i++)
        {
            _ = inner.AddCanFrame(1, FrameBuilders.BuildSocketCanClassic(0x100u + (uint)(i & 0xFF), [(byte)i]), (i + 1) * 1_000L);
        }

        // Raw objects on top level plus one compressed container: both sequential read paths touch the mapping.
        BlfTestGenerator outer = new();
        _ = outer.AddLogContainer(BlfConstants.CompressionLz4, inner);
        for (int i = 0; i < _FrameCount; i++)
        {
            _ = outer.AddCanFrame(2, FrameBuilders.BuildSocketCanClassic(0x200u + (uint)(i & 0xFF), [(byte)i]), (_FrameCount + i + 1) * 1_000L);
        }

        return outer.Build();
    }

    #endregion
}
