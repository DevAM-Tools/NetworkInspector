// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// RFC 6455 §5.2 WebSocket frame opcodes.
/// </summary>
public static class WebSocketOpcode
{
    /// <summary>Continuation frame (opcode 0x0); continues a fragmented message.</summary>
    public const byte Continuation = 0x0;

    /// <summary>Text frame (opcode 0x1); payload is UTF-8 text.</summary>
    public const byte Text = 0x1;

    /// <summary>Binary frame (opcode 0x2); payload is arbitrary binary data.</summary>
    public const byte Binary = 0x2;

    /// <summary>Connection-close control frame (opcode 0x8).</summary>
    public const byte Close = 0x8;

    /// <summary>Ping control frame (opcode 0x9).</summary>
    public const byte Ping = 0x9;

    /// <summary>Pong control frame (opcode 0xA).</summary>
    public const byte Pong = 0xA;
}
