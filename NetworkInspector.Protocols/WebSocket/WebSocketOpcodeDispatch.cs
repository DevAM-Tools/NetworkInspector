// Copyright (c) DevAM and Network Inspector contributors
// Licensed under the MIT license.

namespace NetworkInspector.Protocols;

public sealed partial class WebSocketProtocol
{
    #region Opcode-based payload dispatch (DispatchTextPayload, DispatchBinaryPayload)

    /// <summary>
    /// Dispatches a text-frame payload (opcode 1).
    /// <para><b>Algorithm.</b>
    /// <list type="number">
    ///   <item>Try <c>ws.port</c> table lookup (port-based sub-protocol override).</item>
    ///   <item>If no match, decode payload as UTF-8 and append as <c>websocket.payload.text</c>.</item>
    ///   <item>Forward to the registered <c>text</c> protocol for line-level display.</item>
    /// </list>
    /// </para>
    /// </summary>
    private void DispatchTextPayload(in MutField frameContainer, ReadOnlyMemory<byte> payloadData, in ParseContext context)
    {
        // Try sub-protocol dispatch via ws.port table
        ParseResult portResult = frameContainer.TryCallNextProtocolU64(
            _PortTableId, 0, payloadData, in context);
        if (portResult.IsSuccess && portResult.Value > 0)
        {
            return;
        }

        // Append decoded text
        context.RecordGroupPresence(_WebsocketPayloadTextGroupId);
        string text = System.Text.Encoding.UTF8.GetString(payloadData.Span);
        frameContainer.Append(_PayloadTextField, FieldValue.NewString(text), in context);

        // Forward to Text protocol for line-level display
        if (_TextProtocolId.IsValid)
        {
            frameContainer.Packet.Stack.CallProtocol(_TextProtocolId, in frameContainer, payloadData, in context);
        }
    }

    /// <summary>
    /// Dispatches a binary-frame payload (opcode 2).
    /// <para><b>Algorithm.</b>
    /// <list type="number">
    ///   <item>Try <c>ws.port</c> table lookup (port-based sub-protocol override).</item>
    ///   <item>If no match, forward to the registered <c>data</c> protocol.</item>
    /// </list>
    /// </para>
    /// </summary>
    private void DispatchBinaryPayload(in MutField frameContainer, ReadOnlyMemory<byte> payloadData, in ParseContext context)
    {
        // Try sub-protocol dispatch via ws.port table
        ParseResult portResult = frameContainer.TryCallNextProtocolU64(
            _PortTableId, 0, payloadData, in context);
        if (portResult.IsSuccess && portResult.Value > 0)
        {
            return;
        }

        // Fallback to Data protocol
        if (_DataProtocolId.IsValid)
        {
            frameContainer.Packet.Stack.CallProtocol(_DataProtocolId, in frameContainer, payloadData, in context);
        }
    }

    #endregion
}
