// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Text protocol for displaying plain text payloads as a single string field.
/// Equivalent to Wireshark's "data-text-lines" dissector.
/// Decodes the entire payload as UTF-8 and appends it as one <c>text</c> string field.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// The only instance state is the field and protocol ids, which the generated registration code assigns
/// during the single-threaded build phase and which are read-only thereafter, so <see cref="IProtocol.Parse"/> may be invoked
/// concurrently from any number of threads on the same instance without external synchronisation. Parse keeps no
/// cross-packet state, so it has no first-parse/replay distinction.</para>
/// <para>The text is not split into lines: the whole payload is one string field, decoded eagerly (not through a lazy
/// populator), so each parse allocates one string of the payload's size. The payload size is whatever the calling protocol passes
/// (for example an HTTP body or a WebSocket text message); this protocol applies no limit of its own, so the bound is the
/// caller's.</para>
/// </remarks>
[Protocol("text", "Line-based text data", Description = "Text (line-based)")]
public sealed partial class TextProtocol : IProtocol
{
    #region Constants

    /// <summary>Index group for always-present text fields.</summary>
    private const string _TextIndexGroup = "text";

    #endregion

    #region Fields

    [StringField("text", "Line-based text data", IndexGroup = _TextIndexGroup)]
    private FieldId _TextFieldId;

    /// <summary>
    /// Parses text payload. Decodes the bytes as UTF-8 and appends the result as a single string field.
    /// Invalid UTF-8 sequences are replaced with U+FFFD (the lenient decoder never throws, so untrusted bytes cannot fail the parse).
    /// </summary>
    /// <param name="parentField">Field that receives the <c>text</c> string field.</param>
    /// <param name="data">Payload bytes to decode; an empty payload produces no field.</param>
    /// <param name="context">Parse context used to record index presence.</param>
    /// <returns>0 for an empty payload (nothing consumed), otherwise the number of bytes consumed (all of them).</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        // An empty payload consumes nothing and produces neither a field nor index presence (no empty "text" node).
        if (data.Length == 0)
        {
            return 0;
        }

        // Index presence for the packet index (filters) and the static info text of the packet list.
        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TextGroupId);

        parentField.SetPacketInfo(ZA.Lazy("Text"));

        // Decode the whole payload in one pass. The replacement behaviour of Encoding.UTF8 is what makes arbitrary bytes safe.
        string text = Encoding.UTF8.GetString(data.Span);
        parentField.Append(_TextFieldId, FieldValue.NewString(text));

        return data.Length;
    }
    #endregion
}
