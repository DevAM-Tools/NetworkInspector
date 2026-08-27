// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols;

/// <summary>
/// Text protocol for displaying plain text payloads as a single string field.
/// Equivalent to Wireshark's "data-text-lines" dissector.
/// Decodes the entire payload as UTF-8 and appends it as one <c>text</c> string field.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> instances are immutable after registration completes.
/// All mutable state is initialised inside <c>RegisterFieldsCustom</c> (single-threaded build
/// phase) and is read-only thereafter, so <see cref="IProtocol.Parse"/> may be invoked concurrently from
/// any number of threads on the same instance without external synchronisation.</para>
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
    /// Invalid UTF-8 sequences are replaced with U+FFFD.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context)
    {
        if (data.Length == 0)
        {
            return 0;
        }

        context.RecordProtocolPresence(_ProtocolId);
        context.RecordGroupPresence(_TextGroupId);

        parentField.SetPacketInfo(ZA.Lazy("Text"));

        string text = Encoding.UTF8.GetString(data.Span);
        parentField.Append(_TextFieldId, FieldValue.NewString(text));

        return data.Length;
    }
    #endregion
}
