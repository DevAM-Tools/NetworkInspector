// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Immutable options that control how a single <see cref="WebSocketLayer"/> frame is encoded.
/// </summary>
/// <param name="Fin">
/// FIN bit (RFC 6455 §5.2 bit 7 of byte 0).  <c>true</c> for a non-fragmented message
/// or the final fragment; <c>false</c> for continuation frames.  Default <c>true</c>.
/// </param>
/// <param name="Rsv1">
/// RSV1 bit.  Set to <c>true</c> for the first fragment of a permessage-deflate
/// compressed message (RFC 7692).  Default <c>false</c>.
/// </param>
/// <param name="Rsv2">RSV2 bit; reserved for future use.  Default <c>false</c>.</param>
/// <param name="Rsv3">RSV3 bit; reserved for future use.  Default <c>false</c>.</param>
/// <param name="MaskingKey">
/// 32-bit masking key.  When non-<see langword="null"/>, the MASK bit is set and the
/// payload is XOR-masked (mandatory for client→server frames per RFC 6455 §5.3).
/// <see langword="null"/> = unmasked (server→client direction).
/// </param>
/// <remarks>
/// <para>Thread safety: immutable; safe for concurrent use.</para>
/// </remarks>
public readonly record struct WebSocketFrameOptions(
    bool Fin = true,
    bool Rsv1 = false,
    bool Rsv2 = false,
    bool Rsv3 = false,
    uint? MaskingKey = null);
