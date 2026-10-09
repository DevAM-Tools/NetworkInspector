// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Tests.Helpers;

/// <summary>
/// Registers a lazy container that materializes a single U64 field. Used to prove the filter
/// evaluator's materialize-on-miss pass.
/// </summary>
internal sealed class LazyValueProtocol : IProtocol
{
    private int _PopulateCount;

    /// <summary>Container field id.</summary>
    public FieldId ContainerId;

    /// <summary>Value field id (<c>lazy.value</c>).</summary>
    public FieldId ValueId;

    /// <summary>Value written when the lazy container materializes.</summary>
    public ulong Value { get; set; } = 53;

    /// <summary>How many times the lazy populator ran.</summary>
    public int PopulateCount => _PopulateCount;

    /// <inheritdoc />
    public string Name => "lazy";

    /// <inheritdoc />
    public string UiName => "Lazy";

    /// <summary>Registers container and value fields on the builder.</summary>
    public void RegisterFields(StackBuilder builder, ProtocolId protocolId)
    {
        ContainerId = builder.RegisterField(protocolId, "lazy", "Lazy", FieldType.None);
        ValueId = builder.RegisterField(protocolId, "lazy.value", "Value", FieldType.U64);
    }

    /// <inheritdoc />
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        FieldId valueId = ValueId;
        ulong value = Value;
        parentField.AppendLazy(ContainerId, FieldValue.None, (in MutField container) =>
        {
            System.Threading.Interlocked.Increment(ref _PopulateCount);
            container.Append(valueId, FieldValue.NewU64(value));
            return 0;
        });
        return data.Length;
    }
}
