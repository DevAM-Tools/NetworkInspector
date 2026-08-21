// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Eval;

/// <summary>
/// Comparison semantics shared by comparisons, ranges, sets and flank endpoints.
/// <para>
/// All ordering delegates to <see cref="FieldValueData.CompareTo"/>, which already implements
/// cross-type numeric ordering (<c>I64</c>/<c>U64</c>/<c>F64</c>) and ordinal string ordering.
/// <see cref="FieldValueData.Equals(FieldValueData)"/> is deliberately <b>not</b> used for
/// <see cref="CompareOp.Equal"/> because it requires identical <see cref="FieldType"/> values and
/// would make <c>udp.srcport == 53</c> fail whenever the parser produced a <c>U64</c> literal for
/// an <c>I64</c> field.
/// </para>
/// <para>
/// One coercion is layered on top: booleans compare equal to the numbers <c>0</c> and <c>1</c>, so
/// <c>tcp.flags.syn == 1</c> behaves like <c>tcp.flags.syn == true</c>.
/// </para>
/// </summary>
internal static class FilterCompare
{
    #region Comparison

    /// <summary>Applies a comparison operator to two values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Apply(in FieldValueData left, CompareOp op, in FieldValueData right)
    {
        int order = Compare(left, right);
        return op switch
        {
            CompareOp.Equal => order == 0,
            CompareOp.NotEqual => order != 0,
            CompareOp.LessThan => order < 0,
            CompareOp.LessEqual => order <= 0,
            CompareOp.GreaterThan => order > 0,
            _ => order >= 0,
        };
    }

    /// <summary>Orders two values, coercing booleans against numbers.</summary>
    public static int Compare(in FieldValueData left, in FieldValueData right)
    {
        FieldType leftType = left.Type;
        FieldType rightType = right.Type;

        if (leftType == FieldType.Bool && _IsNumeric(rightType))
        {
            return _AsNumber(left).CompareTo(right);
        }

        if (rightType == FieldType.Bool && _IsNumeric(leftType))
        {
            return left.CompareTo(_AsNumber(right));
        }

        return left.CompareTo(right);
    }

    #endregion

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool _IsNumeric(FieldType type) =>
        type is FieldType.I64 or FieldType.U64 or FieldType.F64;

    private static FieldValueData _AsNumber(in FieldValueData value)
    {
        _ = value.TryGetAsBool(out bool flag);
        return FieldValueData.NewU64(flag ? 1UL : 0UL);
    }

    #endregion
}
