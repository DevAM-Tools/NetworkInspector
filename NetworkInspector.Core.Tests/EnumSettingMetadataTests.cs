// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="EnumSettingMetadata"/> and <see cref="EnumSettingValue"/> —
/// bidirectional lookup, case-insensitivity, and factory methods.
/// </summary>
internal sealed class EnumSettingMetadataTests
{
    [Test]
    public void FromPairs_Null_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(
            () => EnumSettingMetadata.FromPairs(null!));
    }

    [Test]
    public async Task FromPairs_CreatesMetadata()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        await Assert.That(meta.AllowedValues.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GetByNumeric_Found()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1), ("High", 2)]);
        EnumSettingValue? result = meta.GetByNumeric(1);
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
        await Assert.That(result!.Value.NumericValue).IsEqualTo(1UL);
    }

    [Test]
    public async Task GetByNumeric_NotFound()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0)]);
        EnumSettingValue? result = meta.GetByNumeric(99);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetByName_Found_CaseInsensitive()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("Medium", 1)]);
        EnumSettingValue? result = meta.GetByName("medium");
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.Name).IsEqualTo("Medium");
    }

    [Test]
    public async Task GetByName_NotFound()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs([("Low", 0)]);
        EnumSettingValue? result = meta.GetByName("High");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task IsAllowedNumeric_TrueAndFalse()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("High", 1)]);
        await Assert.That(meta.IsAllowedNumeric(0)).IsTrue();
        await Assert.That(meta.IsAllowedNumeric(1)).IsTrue();
        await Assert.That(meta.IsAllowedNumeric(2)).IsFalse();
    }

    [Test]
    public async Task IsAllowedName_TrueAndFalse()
    {
        EnumSettingMetadata meta = EnumSettingMetadata.FromPairs(
            [("Low", 0), ("High", 1)]);
        await Assert.That(meta.IsAllowedName("Low")).IsTrue();
        await Assert.That(meta.IsAllowedName("low")).IsTrue(); // case-insensitive
        await Assert.That(meta.IsAllowedName("Medium")).IsFalse();
    }

    [Test]
    public async Task FromPairs_DuplicateNumeric_Throws()
    {
        await Assert.That(() => EnumSettingMetadata.FromPairs([("Low", 0), ("AlsoLow", 0)]))
            .Throws<ValidationSettingsException>();
    }

    [Test]
    public async Task FromPairs_DuplicateName_Throws()
    {
        await Assert.That(() => EnumSettingMetadata.FromPairs([("Low", 0), ("low", 1)]))
            .Throws<ValidationSettingsException>();
    }

    [Test]
    public async Task EnumSettingValue_Equality()
    {
        EnumSettingValue a = new("Low", 0);
        EnumSettingValue b = new("Low", 0);
        EnumSettingValue c = new("High", 1);
        await Assert.That(a.Equals(b)).IsTrue();
        await Assert.That(a.Equals(c)).IsFalse();
    }

    [Test]
    public async Task EnumSettingValue_ObjectEquals_GetHashCode_ToString_AndOperators()
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
}
