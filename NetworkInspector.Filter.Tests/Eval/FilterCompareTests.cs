// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Eval;

/// <summary>Covers cross-type ordering and boolean coercion used by every value predicate.</summary>
internal sealed class FilterCompareTests
{
    #region Operators

    [Test]
    [Arguments(CompareOp.Equal, 5UL, 5UL, true)]
    [Arguments(CompareOp.Equal, 5UL, 6UL, false)]
    [Arguments(CompareOp.NotEqual, 5UL, 6UL, true)]
    [Arguments(CompareOp.LessThan, 5UL, 6UL, true)]
    [Arguments(CompareOp.LessThan, 6UL, 5UL, false)]
    [Arguments(CompareOp.LessEqual, 5UL, 5UL, true)]
    [Arguments(CompareOp.GreaterThan, 6UL, 5UL, true)]
    [Arguments(CompareOp.GreaterEqual, 5UL, 5UL, true)]
    [Arguments(CompareOp.GreaterEqual, 4UL, 5UL, false)]
    public async Task Apply_ComparesUnsignedValues(CompareOp op, ulong left, ulong right, bool expected)
    {
        bool result = FilterCompare.Apply(FieldValueData.NewU64(left), op, FieldValueData.NewU64(right));

        await Assert.That(result).IsEqualTo(expected);
    }

    #endregion

    #region Cross-type

    [Test]
    public async Task Compare_SignedAgainstUnsigned_OrdersNumerically()
    {
        int order = FilterCompare.Compare(FieldValueData.NewI64(-1), FieldValueData.NewU64(1));

        await Assert.That(order).IsLessThan(0);
    }

    [Test]
    public async Task Compare_FloatAgainstInteger_OrdersNumerically()
    {
        int order = FilterCompare.Compare(FieldValueData.NewF64(2.5), FieldValueData.NewU64(2));

        await Assert.That(order).IsGreaterThan(0);
    }

    [Test]
    public async Task Compare_Strings_UsesOrdinalOrder()
    {
        int order = FilterCompare.Compare(FieldValueData.NewString("a"), FieldValueData.NewString("b"));

        await Assert.That(order).IsLessThan(0);
    }

    #endregion

    #region Boolean coercion

    [Test]
    [Arguments(true, 1UL, 0)]
    [Arguments(true, 0UL, 1)]
    [Arguments(false, 0UL, 0)]
    [Arguments(false, 1UL, -1)]
    public async Task Compare_BooleanOnLeft_CoercesToNumber(bool flag, ulong number, int expectedSign)
    {
        int order = FilterCompare.Compare(FieldValueData.NewBool(flag), FieldValueData.NewU64(number));

        await Assert.That(Math.Sign(order)).IsEqualTo(expectedSign);
    }

    [Test]
    [Arguments(1UL, true, 0)]
    [Arguments(0UL, true, -1)]
    [Arguments(5UL, false, 1)]
    public async Task Compare_BooleanOnRight_CoercesToNumber(ulong number, bool flag, int expectedSign)
    {
        int order = FilterCompare.Compare(FieldValueData.NewU64(number), FieldValueData.NewBool(flag));

        await Assert.That(Math.Sign(order)).IsEqualTo(expectedSign);
    }

    [Test]
    public async Task Compare_TwoBooleans_DoesNotCoerce()
    {
        int order = FilterCompare.Compare(FieldValueData.NewBool(true), FieldValueData.NewBool(true));

        await Assert.That(order).IsEqualTo(0);
    }

    #endregion

    #region Through the language

    [Test]
    [Arguments("ip.flags.df == 1", true)]
    [Arguments("ip.flags.df == 0", false)]
    [Arguments("ip.ttl > true", true)]
    [Arguments("ip.flags.mf == 0", true)]
    public async Task Compare_BooleanFieldsAgainstNumericLiterals(string expression, bool expected)
    {
        using Stack stack = FilterTestHelper.BuildStack();
        Filter filter = FilterTestHelper.CompileOrThrow(expression, stack);
        Packet packet = FilterTestHelper.Parse(stack, FilterTestHelper.BuildUdpFrame(53, 1024));

        await Assert.That(FilterTestHelper.MatchOrThrow(filter, packet)).IsEqualTo(expected);
    }

    #endregion
}
