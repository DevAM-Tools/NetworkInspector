// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Pbf;

/// <summary>
/// Tests for <see cref="PreviousFieldValueStore"/>: same-as-previous detection of byte payloads when the buffer behind an
/// earlier value is reused by the producer.
/// </summary>
internal sealed class PreviousFieldValueStoreTests
{
    private const int _FieldId = 5;

    [Test]
    public async Task BytesValueRepeatedInAnotherArrayIsFlaggedSame()
    {
        PreviousFieldValueStore store = new(32);
        byte[] firstBytes = [1, 2, 3, 4];
        byte[] secondBytes = [1, 2, 3, 4];
        FieldValueData first = FieldValueData.NewBytes(firstBytes);
        FieldValueData second = FieldValueData.NewBytes(secondBytes);

        store.CompareAndUpdate(_FieldId, true, in first, null, null);
        uint flags = store.CompareAndUpdate(_FieldId, true, in second, null, null);

        await Assert.That(flags & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }

    [Test]
    public async Task BytesValueIsNotFlaggedSameWhenTheEarlierBufferWasOverwrittenWithTheNewContent()
    {
        PreviousFieldValueStore store = new(32);
        byte[] reusedBuffer = [1, 2, 3, 4];
        FieldValueData first = FieldValueData.NewBytes(reusedBuffer);
        store.CompareAndUpdate(_FieldId, true, in first, null, null);

        // The producer reuses its buffer for the next packet: the first value's bytes are now {9,9,9,9}, and so is the
        // second value. The store must still know that the first value was {1,2,3,4}.
        reusedBuffer.AsSpan().Fill(9);
        byte[] secondBytes = [9, 9, 9, 9];
        FieldValueData second = FieldValueData.NewBytes(secondBytes);
        uint flags = store.CompareAndUpdate(_FieldId, true, in second, null, null);

        await Assert.That(flags & SameFlags.FieldSameValue).IsEqualTo(0u);
    }

    [Test]
    public async Task BytesValueEqualToTheOriginalContentIsStillFlaggedSameAfterTheBufferWasOverwritten()
    {
        PreviousFieldValueStore store = new(32);
        byte[] reusedBuffer = [1, 2, 3, 4];
        FieldValueData first = FieldValueData.NewBytes(reusedBuffer);
        store.CompareAndUpdate(_FieldId, true, in first, null, null);

        reusedBuffer.AsSpan().Fill(9);
        byte[] secondBytes = [1, 2, 3, 4];
        FieldValueData second = FieldValueData.NewBytes(secondBytes);
        uint flags = store.CompareAndUpdate(_FieldId, true, in second, null, null);

        await Assert.That(flags & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }

    [Test]
    public async Task EmptyBytesValueIsComparedWithoutError()
    {
        PreviousFieldValueStore store = new(32);
        FieldValueData first = FieldValueData.NewBytes(Array.Empty<byte>());
        FieldValueData second = FieldValueData.NewBytes(Array.Empty<byte>());

        store.CompareAndUpdate(_FieldId, true, in first, null, null);
        uint flags = store.CompareAndUpdate(_FieldId, true, in second, null, null);

        await Assert.That(flags & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }

    [Test]
    public async Task BytesValueGrowingInLengthIsNotFlaggedSame()
    {
        PreviousFieldValueStore store = new(32);
        byte[] shortBytes = [1, 2];
        byte[] longBytes = [1, 2, 3, 4, 5, 6];
        byte[] longBytesAgain = [1, 2, 3, 4, 5, 6];
        FieldValueData first = FieldValueData.NewBytes(shortBytes);
        FieldValueData second = FieldValueData.NewBytes(longBytes);
        FieldValueData third = FieldValueData.NewBytes(longBytesAgain);

        store.CompareAndUpdate(_FieldId, true, in first, null, null);
        uint growing = store.CompareAndUpdate(_FieldId, true, in second, null, null);
        uint repeated = store.CompareAndUpdate(_FieldId, true, in third, null, null);

        await Assert.That(growing & SameFlags.FieldSameValue).IsEqualTo(0u);
        await Assert.That(repeated & SameFlags.FieldSameValue).IsEqualTo(SameFlags.FieldSameValue);
    }
}
