// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace Sample.Protocol;

/// <summary>
/// Post-parser that runs after the main parse of every packet and, when the frame carries the NIS1 magic anywhere, adds the
/// root-level field <c>sample.post.magic_offset</c> with the byte offset of the first occurrence in the frame. It shows that a
/// plugin can annotate packets that no protocol of the plugin dispatched. Thread safety: stateless after
/// <see cref="RegisterFields"/>.
/// </summary>
internal sealed class SamplePostParser : IProtocol
{
    #region Fields

    private FieldId _MagicOffsetFieldId;

    #endregion

    #region IProtocol

    /// <inheritdoc/>
    public string Name => "sample.post";

    /// <inheritdoc/>
    public string UiName => "Sample marker";

    /// <inheritdoc/>
    public string? Description => "Marks frames that carry the NIS1 magic.";

    /// <inheritdoc/>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // The data of a post-parser is the whole frame; nothing is consumed (result 0), the field is only an annotation.
        int offset = data.Span.IndexOf(SampleWire.MagicBytes);
        if (offset >= 0)
        {
            parentField.Append(_MagicOffsetFieldId, FieldValue.NewU64((ulong)offset));
        }

        return 0;
    }

    #endregion

    #region Internal API

    /// <summary>Registers the field of the post-parser; called once, right after the protocol registration.</summary>
    /// <param name="stack">The builder that registered this protocol.</param>
    /// <param name="protocolId">The id the builder assigned to this protocol.</param>
    internal void RegisterFields(IStackBuilder stack, ProtocolId protocolId)
    {
        _MagicOffsetFieldId = stack.RegisterField(protocolId, "sample.post.magic_offset", "NIS1 offset", FieldType.U64,
            "Byte offset of the first NIS1 magic in the frame.");
    }

    #endregion
}
