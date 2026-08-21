// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Eval;

/// <summary>Covers slice and length transformations for every value shape they support.</summary>
internal sealed class ValueAccessorTests
{
    #region Direct

    [Test]
    public async Task Direct_PassesValueThrough()
    {
        ValueAccessor accessor = ValueAccessor.Direct([new FieldId(1)]);

        bool transformed = accessor.TryTransform(FieldValueData.NewU64(7), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        await Assert.That(value.Type).IsEqualTo(FieldType.U64);
        await Assert.That(accessor.Kind).IsEqualTo(ValueAccessorKind.Direct);
        await Assert.That(accessor.Fields.Length).IsEqualTo(1);
    }

    #endregion

    #region Slice

    [Test]
    public async Task Slice_OnBytes_ReturnsRequestedRange()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 1, 3);
        byte[] source = [0x11, 0x22, 0x33, 0x44];

        bool transformed = accessor.TryTransform(FieldValueData.NewBytes(source), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        await Assert.That(value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes)).IsTrue();
        await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 0x22, 0x33 });
        await Assert.That(accessor.SliceStart).IsEqualTo(1);
        await Assert.That(accessor.SliceEnd).IsEqualTo(3);
    }

    [Test]
    public async Task Slice_PastEnd_Fails()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 8);
        byte[] source = [0x11, 0x22];

        bool transformed = accessor.TryTransform(FieldValueData.NewBytes(source), out _);

        await Assert.That(transformed).IsFalse();
    }

    [Test]
    public async Task Slice_OnMacAddress_UsesNetworkOrder()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 3);
        _ = MacAddress.TryParse("00:11:22:33:44:55", out MacAddress mac);

        bool transformed = accessor.TryTransform(FieldValueData.NewMacAddress(mac), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x11, 0x22 });
    }

    [Test]
    public async Task Slice_OnIPv4Address_UsesNetworkOrder()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 2);
        _ = IPv4Address.TryParse("192.168.1.10", out IPv4Address address);

        bool transformed = accessor.TryTransform(FieldValueData.NewIPv4(address), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 192, 168 });
    }

    [Test]
    public async Task Slice_OnIPv6Address_UsesNetworkOrder()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 2);
        _ = IPv6Address.TryParse("2001:db8::1", out IPv6Address address);

        bool transformed = accessor.TryTransform(FieldValueData.NewIPv6(address), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 0x20, 0x01 });
    }

    [Test]
    public async Task Slice_OnEui64_UsesNetworkOrder()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 2);

        bool transformed = accessor.TryTransform(
            FieldValueData.NewEui64(new Eui64(0x0011223344556677UL)),
            out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsBytes(out ReadOnlyMemory<byte> bytes);
        await Assert.That(bytes.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x11 });
    }

    [Test]
    public async Task Slice_OnUnsupportedType_Fails()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 2);

        await Assert.That(accessor.TryTransform(FieldValueData.NewU64(5), out _)).IsFalse();
    }

    [Test]
    public async Task Slice_ReusesItsBuffer()
    {
        ValueAccessor accessor = ValueAccessor.Slice([new FieldId(1)], 0, 2);

        byte[] firstSource = [1, 2, 3];
        byte[] secondSource = [9, 8, 7];

        _ = accessor.TryTransform(FieldValueData.NewBytes(firstSource), out FieldValueData first);
        _ = first.TryGetAsBytes(out ReadOnlyMemory<byte> firstBytes);
        _ = accessor.TryTransform(FieldValueData.NewBytes(secondSource), out FieldValueData second);
        _ = second.TryGetAsBytes(out ReadOnlyMemory<byte> secondBytes);

        await Assert.That(firstBytes.ToArray()).IsEquivalentTo(new byte[] { 9, 8 });
        await Assert.That(secondBytes.ToArray()).IsEquivalentTo(new byte[] { 9, 8 });
    }

    #endregion

    #region Length

    [Test]
    public async Task Length_OnBytes_CountsBytes()
    {
        ValueAccessor accessor = ValueAccessor.Length([new FieldId(1)]);
        byte[] source = [1, 2, 3];

        bool transformed = accessor.TryTransform(FieldValueData.NewBytes(source), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsU64(out ulong length);
        await Assert.That(length).IsEqualTo(3UL);
    }

    [Test]
    public async Task Length_OnString_CountsCharacters()
    {
        ValueAccessor accessor = ValueAccessor.Length([new FieldId(1)]);

        bool transformed = accessor.TryTransform(FieldValueData.NewString("abcd"), out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsU64(out ulong length);
        await Assert.That(length).IsEqualTo(4UL);
    }

    [Test]
    [Arguments(FieldType.Bool, 1UL)]
    [Arguments(FieldType.U64, 8UL)]
    [Arguments(FieldType.MacAddress, 6UL)]
    [Arguments(FieldType.IPv4Address, 4UL)]
    [Arguments(FieldType.IPv6Address, 16UL)]
    public async Task Length_OnFixedWidthValues_UsesTypeWidth(FieldType type, ulong expected)
    {
        ValueAccessor accessor = ValueAccessor.Length([new FieldId(1)]);
        FieldValueData raw = _Sample(type);

        bool transformed = accessor.TryTransform(raw, out FieldValueData value);

        await Assert.That(transformed).IsTrue();
        _ = value.TryGetAsU64(out ulong length);
        await Assert.That(length).IsEqualTo(expected);
    }

    [Test]
    public async Task Length_OnValueWithoutWidth_Fails()
    {
        ValueAccessor accessor = ValueAccessor.Length([new FieldId(1)]);

        await Assert.That(accessor.TryTransform(default, out _)).IsFalse();
    }

    #endregion

    #region Helpers

    private static FieldValueData _Sample(FieldType type)
    {
        switch (type)
        {
            case FieldType.Bool:
                return FieldValueData.NewBool(true);

            case FieldType.MacAddress:
                _ = MacAddress.TryParse("00:11:22:33:44:55", out MacAddress mac);
                return FieldValueData.NewMacAddress(mac);

            case FieldType.IPv4Address:
                _ = IPv4Address.TryParse("1.2.3.4", out IPv4Address ipv4);
                return FieldValueData.NewIPv4(ipv4);

            case FieldType.IPv6Address:
                _ = IPv6Address.TryParse("::1", out IPv6Address ipv6);
                return FieldValueData.NewIPv6(ipv6);

            default:
                return FieldValueData.NewU64(1);
        }
    }

    #endregion
}
