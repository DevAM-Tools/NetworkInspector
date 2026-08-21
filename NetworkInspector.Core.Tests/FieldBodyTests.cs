// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>Exit-point coverage for internal <see cref="FieldBody"/>.</summary>
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
    public async Task FieldBody_LazyIndex_GetSet_Roundtrip()
    {
        FieldBody body = new(new FieldId(4))
        {
            LazyIndex = 7,
        };

        await Assert.That(body.LazyIndex).IsEqualTo((ushort)7);
    }

    [Test]
    public async Task FieldBody_IsLazyMaterializationInProgress_WhenMaterializingBitSet()
    {
        FieldBody body = new(new FieldId(5))
        {
            LazyIndex = (ushort)(3 | FieldBody.LazyIndexMaterializingBit),
        };

        await Assert.That(body.IsLazyMaterializationInProgress()).IsTrue();
        await Assert.That(body.NeedsMaterialization).IsFalse();
    }

    [Test]
    public async Task FieldBody_TryClaimLazyMaterialization_SucceedsAndSetsMaterializingBit()
    {
        FieldBody body = new(new FieldId(6))
        {
            LazyIndex = 4,
        };

        bool claimed = body.TryClaimLazyMaterialization(out ushort populatorIndex);

        await Assert.That(claimed).IsTrue();
        await Assert.That(populatorIndex).IsEqualTo((ushort)4);
        await Assert.That(body.IsLazyMaterializationInProgress()).IsTrue();
    }

    [Test]
    public async Task FieldBody_TryClaimLazyMaterialization_FailsWhenUnsetOrAlreadyMaterializing()
    {
        FieldBody unset = new(new FieldId(7));
        bool unsetClaim = unset.TryClaimLazyMaterialization(out ushort unsetIndex);
        await Assert.That(unsetClaim).IsFalse();
        await Assert.That(unsetIndex).IsEqualTo((ushort)0);

        FieldBody inProgress = new(new FieldId(8))
        {
            LazyIndex = (ushort)(2 | FieldBody.LazyIndexMaterializingBit),
        };
        bool inProgressClaim = inProgress.TryClaimLazyMaterialization(out ushort inProgressIndex);
        await Assert.That(inProgressClaim).IsFalse();
        await Assert.That(inProgressIndex).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task FieldBody_TryClaimLazyMaterialization_ConcurrentCasCollision_ReturnsFalse()
    {
        FieldBody[] buffer = new FieldBody[4];
        const int offset = 0;
        buffer[offset] = new FieldBody(new FieldId(11))
        {
            LazyIndex = 7,
        };

        int casFailures = 0;
        int successes = 0;
        for (int attempt = 0; attempt < 512; attempt++)
        {
            buffer[offset] = new FieldBody(new FieldId(11))
            {
                LazyIndex = 7,
            };
            using Barrier start = new(2);
            int attemptSuccesses = 0;
            int attemptFailures = 0;

            Task first = Task.Run(() =>
            {
                start.SignalAndWait();
                ref FieldBody body = ref buffer[offset];
                if (body.TryClaimLazyMaterialization(out _))
                {
                    Interlocked.Increment(ref attemptSuccesses);
                }
            });
            Task second = Task.Run(() =>
            {
                start.SignalAndWait();
                ref FieldBody body = ref buffer[offset];
                if (body.TryClaimLazyMaterialization(out _))
                {
                    Interlocked.Increment(ref attemptSuccesses);
                }
                else
                {
                    Interlocked.Increment(ref attemptFailures);
                }
            });

            await Task.WhenAll(first, second);
            successes += attemptSuccesses;
            casFailures += attemptFailures;
        }

        await Assert.That(successes).IsGreaterThan(0);
        await Assert.That(casFailures).IsGreaterThan(0);
    }

    [Test]
    public async Task FieldBody_TryClaimLazyMaterialization_ConcurrentSecondClaim_ReturnsFalse()
    {
        FieldBody body = new(new FieldId(9))
        {
            LazyIndex = 5,
        };
        using ManualResetEventSlim gate = new(false);
        bool secondClaim = false;

        Task first = Task.Run(() =>
        {
            bool claimed = body.TryClaimLazyMaterialization(out ushort index);
            if (claimed)
            {
                gate.Set();
                Thread.Sleep(50);
                body.FinishLazyMaterialization();
            }
        });

        Task second = Task.Run(() =>
        {
            gate.Wait();
            secondClaim = body.TryClaimLazyMaterialization(out _);
        });

        await Task.WhenAll(first, second);
        await Assert.That(secondClaim).IsFalse();
    }

    [Test]
    public async Task FieldBody_TryClaimLazyMaterialization_AfterMaterializingBitSet_ReturnsFalse()
    {
        FieldBody body = new(new FieldId(10))
        {
            LazyIndex = 6,
        };
        System.Reflection.FieldInfo? lazyField = typeof(FieldBody).GetField(
            "_LazyIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        await Assert.That(lazyField).IsNotNull();

        object boxed = body;
        lazyField!.SetValue(boxed, (int)(6 | FieldBody.LazyIndexMaterializingBit));
        body = (FieldBody)boxed;

        bool claimed = body.TryClaimLazyMaterialization(out ushort index);
        await Assert.That(claimed).IsFalse();
        await Assert.That(index).IsEqualTo((ushort)0);
    }
}
