// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for internal <see cref="FieldBody"/> and <see cref="FieldBodySlab"/> types.</summary>
internal sealed class FieldBodyTests
{
    [Test]
    public async Task FieldBody_ValueAndLinkMutators_Roundtrip()
    {
        FieldId fieldId = new(7);
        FieldBody body = new(fieldId, FieldValue.NewU64(42));

        body.Value = FieldValue.NewU64(99);
        body.PrevIndex = 3;
        body.ChildCount = 2;

        await Assert.That(body.FieldId).IsEqualTo(fieldId);
        await Assert.That(body.Value.Data.TryGetAsU64(out ulong value)).IsTrue();
        await Assert.That(value).IsEqualTo(99UL);
        await Assert.That(body.PrevIndex).IsEqualTo((ushort)3);
        await Assert.That(body.ChildCount).IsEqualTo((ushort)2);
    }

    [Test]
    public async Task FieldBody_CustomText_ClearAndAppend()
    {
        FieldBody body = new(new FieldId(1));
        body.SetCustomText("hello");
        _ = body.CustomText;

        body.AppendCustomText(" world");
        await Assert.That(body.CustomText.AsString).IsEqualTo("hello world");

        body.ClearCustomText();
        await Assert.That(body.CustomText.IsNull).IsTrue();
    }

    [Test]
    public async Task FieldBody_AppendCustomText_OnEmptyBody_SetsSuffix()
    {
        FieldBody body = new(new FieldId(2));
        body.AppendCustomText("only");

        await Assert.That(body.CustomText.AsString).IsEqualTo("only");
    }

    [Test]
    public async Task FieldBody_IncrementChildCount_ThrowsAtLimit()
    {
        FieldBody body = new(new FieldId(3))
        {
            ChildCount = ushort.MaxValue,
        };

        await Assert.That(() => body.IncrementChildCount()).Throws<OverflowException>();
    }

    [Test]
    public async Task FieldBodySlab_TryAllocate_SucceedsWithinCapacity()
    {
        FieldBodySlab slab = new(4);

        bool allocated = slab.TryAllocate(2, out FieldBody[] buffer, out int offset);

        await Assert.That(allocated).IsTrue();
        await Assert.That(buffer.Length).IsEqualTo(4);
        await Assert.That(offset).IsEqualTo(0);

        bool second = slab.TryAllocate(2, out _, out int secondOffset);
        await Assert.That(second).IsTrue();
        await Assert.That(secondOffset).IsEqualTo(2);
    }

    [Test]
    public async Task FieldBodySlab_TryAllocate_FailsWhenFull()
    {
        FieldBodySlab slab = new(2);

        await Assert.That(slab.TryAllocate(2, out _, out _)).IsTrue();
        await Assert.That(slab.TryAllocate(1, out FieldBody[] buffer, out int offset)).IsFalse();
        await Assert.That(buffer).IsNull();
        await Assert.That(offset).IsEqualTo(0);
    }
}
