// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings.Tests;

/// <summary>
/// Tests for <see cref="EnumSettingMetadata"/> and <see cref="EnumSettingValue"/> —
/// bidirectional lookup, case-insensitivity, and factory methods.
/// </summary>
internal sealed class EnumSettingMetadataTests
{
    [Test]
    public void FromPairsNullThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => EnumSettingMetadata.FromPairs(null!));
    }

    [Test]
    public async Task FromPairsCreatesMetadata()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        await Assert.That(meta.AllowedValues.Count).IsEqualTo(3);
    }

    [Test]
    public async Task AllowedValuesIsReadOnlyAndNotTheInternalArray()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0), ("High", 1)]);

        bool isArray = meta.AllowedValues is EnumSettingValue[];
        bool readOnly = meta.AllowedValues is IList<EnumSettingValue> list && list.IsReadOnly;
        bool sameInstance = ReferenceEquals(meta.AllowedValues, meta.AllowedValues);

        await Assert.That(isArray).IsFalse();
        await Assert.That(readOnly).IsTrue();
        await Assert.That(sameInstance).IsTrue();
        await Assert.That(meta.AllowedValues[1].Name).IsEqualTo("High");
    }

    [Test]
    public async Task GetByNumericFound()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        EnumSettingValue? result = meta.GetByNumeric(1);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
        await Assert.That(result!.Value.NumericValue).IsEqualTo(1UL);
    }

    [Test]
    public async Task GetByNumericNotFound()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0)]);
        EnumSettingValue? result = meta.GetByNumeric(99);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetByNameFoundCaseInsensitive()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1)]);
        EnumSettingValue? result = meta.GetByName("medium");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
    }

    [Test]
    public async Task GetByNameNotFound()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0)]);
        EnumSettingValue? result = meta.GetByName("High");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task IsAllowedNumericTrueAndFalse()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("High", 1)]);
        await Assert.That(meta.IsAllowedNumeric(0)).IsTrue();
        await Assert.That(meta.IsAllowedNumeric(1)).IsTrue();
        await Assert.That(meta.IsAllowedNumeric(2)).IsFalse();
    }

    [Test]
    public async Task IsAllowedNameTrueAndFalse()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("High", 1)]);
        await Assert.That(meta.IsAllowedName("Low")).IsTrue();
        await Assert.That(meta.IsAllowedName("low")).IsTrue(); // case-insensitive
        await Assert.That(meta.IsAllowedName("Medium")).IsFalse();
    }

    [Test]
    public async Task FromPairsDuplicateNumericThrows()
    {
        await Assert.That(() => EnumSettingMetadata.FromPairs([("Low", 0), ("AlsoLow", 0)]))
            .Throws<ValidationSettingsException>();
    }

    [Test]
    public async Task FromPairsDuplicateNameThrows()
    {
        await Assert.That(() => EnumSettingMetadata.FromPairs([("Low", 0), ("low", 1)]))
            .Throws<ValidationSettingsException>();
    }

    [Test]
    public async Task EnumSettingValueEquality()
    {
        EnumSettingValue a = new("Low", 0);
        EnumSettingValue b = new("Low", 0);
        EnumSettingValue c = new("High", 1);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
    }

    [Test]
    public async Task EnumSettingValueObjectEqualsGetHashCodeToStringAndOperators()
    {
        EnumSettingValue a = new("Medium", 1);
        EnumSettingValue b = new("Medium", 1);
        EnumSettingValue c = new("High", 2);

        await Assert.That(a.Equals((object)b)).IsTrue();
        await Assert.That(a.Equals((object)c)).IsFalse();
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.ToString()).IsEqualTo("Medium");
        await Assert.That(a == b).IsTrue();
        await Assert.That(a != c).IsTrue();
    }

    [Test]
    public async Task ADefaultElementIsRefusedWithTheIndex()
    {
        ValidationSettingsException exception = Assert.Throws<ValidationSettingsException>(() => _ = new EnumSettingMetadata([default]));

        await Assert.That(exception.Message).Contains("index 0");
        await Assert.That(exception.Message).Contains("no name");
    }

    [Test]
    public async Task ADefaultElementAfterValidOnesIsRefusedWithItsOwnIndex()
    {
        ValidationSettingsException exception = Assert.Throws<ValidationSettingsException>(
            () => _ = new EnumSettingMetadata([new EnumSettingValue("a", 1), default]));

        await Assert.That(exception.Message).Contains("index 1");
    }
}
