// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Ast;

#region Base

/// <summary>
/// A value-producing node that may appear on the left-hand side of a comparison,
/// set test, range test or string predicate.
/// </summary>
internal abstract class OperandNode : FilterNode
{
    /// <summary>Records the operand's source span.</summary>
    protected OperandNode(int position, int length)
        : base(position, length)
    {
    }

    /// <summary>Name of the field or alias the operand reads.</summary>
    public abstract string Name { get; }
}

#endregion

#region Concrete operands

/// <summary>A plain qualified field or alias reference such as <c>udp.port</c>.</summary>
internal sealed class FieldOperandNode(string name, int position, int length)
    : OperandNode(position, length)
{
    /// <inheritdoc />
    public override string Name { get; } = name;
}

/// <summary>
/// A byte slice of a field value, e.g. <c>eth.src[0:3]</c>.
/// The range is half-open: <c>[Start, End)</c>.
/// </summary>
internal sealed class SliceOperandNode(string name, int start, int end, int position, int length)
    : OperandNode(position, length)
{
    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <summary>Inclusive start byte offset.</summary>
    public int Start { get; } = start;

    /// <summary>Exclusive end byte offset.</summary>
    public int End { get; } = end;
}

/// <summary>The byte length of a field value, e.g. <c>len(udp.payload)</c>.</summary>
internal sealed class LengthOperandNode(string name, int position, int length)
    : OperandNode(position, length)
{
    /// <inheritdoc />
    public override string Name { get; } = name;
}

#endregion
