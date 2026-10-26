// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests;

/// <summary>
/// Tests for <see cref="PooledBuffer"/>: size limits and truncation.
/// </summary>
internal sealed class PooledBufferTests
{
    [Test]
    public async Task ReserveBeyondTheArrayLimitFailsWithAMessageThatNamesTheLimit()
    {
        PooledBuffer buffer = new(16);
        buffer.Write(new byte[10]);
        InvalidOperationException? failure = null;
        try
        {
            // 10 bytes are in the buffer, so this sum wraps past int.MaxValue.
            buffer.Reserve(int.MaxValue);
        }
        catch (InvalidOperationException ex)
        {
            failure = ex;
        }
        finally
        {
            buffer.Return();
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("Array.MaxLength");
    }

    [Test]
    public async Task ReserveJustBeyondTheArrayLimitFailsAndLeavesTheBufferUnchanged()
    {
        PooledBuffer buffer = new(16);
        buffer.Write(new byte[10]);
        int lengthBefore = buffer.Length;
        bool failed = false;
        try
        {
            buffer.Reserve(Array.MaxLength);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        int lengthAfter = buffer.Length;
        buffer.Return();

        await Assert.That(failed).IsTrue();
        await Assert.That(lengthAfter).IsEqualTo(lengthBefore);
    }

    [Test]
    public async Task WriteWithinTheLimitStillGrowsTheBuffer()
    {
        PooledBuffer buffer = new(4);

        buffer.Write(new byte[1000]);
        buffer.WriteByte(7);

        int length = buffer.Length;
        byte last = buffer.WrittenSpan[1000];
        buffer.Return();

        await Assert.That(length).IsEqualTo(1001);
        await Assert.That(last).IsEqualTo((byte)7);
    }

    [Test]
    public async Task TruncateToDropsTheBytesAfterTheGivenLength()
    {
        PooledBuffer buffer = new(16);
        buffer.Write(new byte[] { 1, 2, 3, 4, 5 });

        buffer.TruncateTo(2);
        buffer.WriteByte(9);

        byte[] content = buffer.WrittenSpan.ToArray();
        buffer.Return();

        await Assert.That(content).IsEquivalentTo(new byte[] { 1, 2, 9 });
    }

    [Test]
    [Arguments(-1)]
    [Arguments(6)]
    public async Task TruncateToRejectsALengthOutsideTheWrittenRange(int length)
    {
        PooledBuffer buffer = new(16);
        buffer.Write(new byte[5]);
        try
        {
            await Assert.That(() => buffer.TruncateTo(length)).Throws<ArgumentOutOfRangeException>();
        }
        finally
        {
            buffer.Return();
        }
    }
}
