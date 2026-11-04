// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Tests.Json;

/// <summary>
/// <see cref="PreviousFieldStore"/>: same-as-previous flags for typed values and texts, the dictionary representation above the
/// dense threshold, the retention rule (the store keeps built strings, never a deferred formatter), and the guard of its constructor.
/// </summary>
internal sealed class PreviousFieldStoreTests
{
    private const int _FieldId = 4;

    /// <summary>A deferred text over a state object, the shape a dissector hands out; building it counts the calls.</summary>
    private static LazyString _Deferred(string text, int[] builds) =>
        LazyString.FormatLazy(
            (text, builds),
            static state =>
            {
                state.builds[0]++;
                return state.text;
            });

    [Test]
    [Arguments(8)]
    [Arguments(5000)]
    public async Task StoredTextsAreStringBacked(int fieldCount)
    {
        PreviousFieldStore store = new(fieldCount);
        int[] builds = [0];
        FieldValue value = FieldValue.NewU64(1);

        _ = store.CompareAndUpdate(_FieldId, value, _Deferred("repr", builds), _Deferred("text", builds));
        _ = store.CompareAndUpdate(_FieldId, value, _Deferred("repr", builds), _Deferred("text", builds));
        bool found = store.TryGetStoredTexts(_FieldId, out LazyString representation, out LazyString text);

        await Assert.That(found).IsTrue();
        await Assert.That(representation.RawValue is string).IsTrue();
        await Assert.That(text.RawValue is string).IsTrue();
        await Assert.That(text.AsString).IsEqualTo("text");
    }

    [Test]
    public async Task OutOfRangeFieldIdsStoreStringBackedTexts()
    {
        PreviousFieldStore store = new(2);
        int[] builds = [0];
        FieldValue value = FieldValue.NewU64(1);

        _ = store.CompareAndUpdate(99, value, _Deferred("repr", builds), _Deferred("text", builds));
        bool found = store.TryGetStoredTexts(99, out LazyString representation, out LazyString text);

        await Assert.That(found).IsTrue();
        await Assert.That(representation.RawValue is string).IsTrue();
        await Assert.That(text.RawValue is string).IsTrue();
    }

    [Test]
    [Arguments(8)]
    [Arguments(5000)]
    public async Task FlagsFollowTheTextsWhetherTheyAreDeferredOrBuilt(int fieldCount)
    {
        PreviousFieldStore store = new(fieldCount);
        int[] builds = [0];
        FieldValue value = FieldValue.NewU64(1);

        uint first = store.CompareAndUpdate(_FieldId, value, _Deferred("repr", builds), _Deferred("text", builds));
        uint same = store.CompareAndUpdate(_FieldId, value, new LazyString("repr"), _Deferred("text", builds));
        uint changed = store.CompareAndUpdate(_FieldId, value, new LazyString("other"), default);

        await Assert.That(first).IsEqualTo(0u);
        await Assert.That(same).IsEqualTo(SameFlags.FieldSameValue | SameFlags.FieldSameCustomRepresentation | SameFlags.FieldSameCustomText);
        await Assert.That(changed).IsEqualTo(SameFlags.FieldSameValue);
    }

    /// <summary>
    /// A <c>fieldCount</c> above the dense threshold (2048) selects the dictionary representation, which must still detect
    /// same-as-previous values.
    /// </summary>
    [Test]
    public async Task SparseModeDetectsSameAsPrevious()
    {
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;
        FieldValue hello = FieldValue.NewString("hello");

        // First call: nothing stored yet, no same flags. Second call with the same value: the value is flagged.
        uint first = store.CompareAndUpdate(fieldId, hello, default, default);
        uint second = store.CompareAndUpdate(fieldId, hello, default, default);

        await Assert.That((first & SameFlags.FieldSameValue) != 0).IsFalse();
        await Assert.That((second & SameFlags.FieldSameValue) != 0).IsTrue();
    }

    [Test]
    public async Task SparseModeDetectsValueChange()
    {
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;

        _ = store.CompareAndUpdate(fieldId, FieldValue.NewString("hello"), default, default);
        uint flags = store.CompareAndUpdate(fieldId, FieldValue.NewString("world"), default, default);

        await Assert.That((flags & SameFlags.FieldSameValue) != 0).IsFalse();
    }

    [Test]
    public async Task SparseModeResetClearsPreviousValues()
    {
        PreviousFieldStore store = new(4096);
        int fieldId = 3000;
        FieldValue hello = FieldValue.NewString("hello");
        _ = store.CompareAndUpdate(fieldId, hello, default, default);

        store.Reset();

        // After the reset the value appears new again.
        uint flags = store.CompareAndUpdate(fieldId, hello, default, default);
        await Assert.That((flags & SameFlags.FieldSameValue) != 0).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task ANegativeFieldCountIsRefusedAndNamed(int fieldCount)
    {
        await Assert.That(() => new PreviousFieldStore(fieldCount))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("fieldCount");
        await Assert.That(() => new PreviousFieldValueStore(fieldCount))
            .Throws<ArgumentOutOfRangeException>()
            .WithMessageContaining("fieldCount");
    }
}
