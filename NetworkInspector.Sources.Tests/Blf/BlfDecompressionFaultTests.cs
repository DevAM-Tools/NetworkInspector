// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Sources.Tests.Blf;

/// <summary>
/// A decompression that fails with an exception type nobody planned for must not leave its in-flight entry behind:
/// threads waiting for that container would block forever. The fault is injected by disposing the decompression
/// semaphore of the source (private state, reached by reflection), which makes the winner's wait throw
/// <see cref="ObjectDisposedException"/> after the entry was registered.
/// </summary>
internal sealed class BlfDecompressionFaultTests
{
    private static readonly TimeSpan _Bound = TimeSpan.FromSeconds(30);

    private static readonly byte[] _CanFrame = TestFrames.BuildSocketCanClassic(0x123, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

    private static byte[] _BuildFile()
    {
        BlfCaptureBuilder outer = new();
        for (int container = 0; container < 2; container++)
        {
            BlfCaptureBuilder inner = new();
            for (int frame = 0; frame < 4; frame++)
            {
                inner.AddCanFrame(1, _CanFrame, ((container * 4) + frame + 1) * 1_000_000L);
            }

            outer.AddLogContainer(BlfConstants.CompressionZlib, inner);
        }

        return outer.Build();
    }

    private static T _Field<T>(BlfSource source, string name)
        where T : class
    {
        System.Reflection.FieldInfo field = typeof(BlfSource).GetField(
            name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(Invariant($"Field {name} not found."));
        return (T)field.GetValue(source)!;
    }

    private static int _PendingCount(BlfSource source) =>
        ((System.Collections.IDictionary)_Field<object>(source, "_PendingDecompressions")).Count;

    private static BlfSource _OpenWithBrokenSemaphore()
    {
        BlfSource source = BlfSource.FromData(_BuildFile(), "fault.blf", new BlfSourceOptions { ScanMode = ScanMode.Full, CacheBudget = 0 });
        SourceTestFixture.InitializeAndStartSource(source);
        _Field<SemaphoreSlim>(source, "_DecompressionSemaphore").Dispose();
        return source;
    }

    [Test]
    public async Task RandomAccessWinnerRemovesItsEntryWhenDecompressionFailsUnexpectedly()
    {
        using BlfSource source = _OpenWithBrokenSemaphore();

        await Assert.That(() => source.FrameById(0)).Throws<ObjectDisposedException>();

        await Assert.That(_PendingCount(source)).IsEqualTo(0);
    }

    [Test]
    public async Task RandomAccessReaderDoesNotHangOnAContainerWhoseWinnerFailedUnexpectedly()
    {
        using BlfSource source = _OpenWithBrokenSemaphore();
        await Assert.That(() => source.FrameById(0)).Throws<ObjectDisposedException>();

        // A second reader of the same container must become the winner again (and fail the same way), not wait for a
        // signal that never comes.
        Task<bool> second = Task.Run(() =>
        {
            try
            {
                _ = source.FrameById(1);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });

        // WaitAsync bounds the wait: a hang fails the test with a TimeoutException instead of blocking the run.
        bool failedAgain = await second.WaitAsync(_Bound);

        await Assert.That(failedAgain).IsTrue();
    }

    [Test]
    public async Task SequentialWinnerRemovesItsEntryWhenDecompressionFailsUnexpectedly()
    {
        using BlfSource source = _OpenWithBrokenSemaphore();

        await Assert.That(() => source.NextFrame()).Throws<ObjectDisposedException>();

        await Assert.That(_PendingCount(source)).IsEqualTo(0);
    }
}
