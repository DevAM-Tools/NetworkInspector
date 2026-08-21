// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.FrameBuilder;

/// <summary>
/// Immutable container for serialized TCP option bytes.
/// </summary>
/// <remarks>
/// Produced by <see cref="TcpOptionsBuilder.Build"/>.
/// Use <see cref="Empty"/> when no options are needed.
/// <para>Thread safety: immutable; safe for concurrent use.</para>
/// </remarks>
/// <param name="Data">The raw option bytes, already padded to a 4-byte boundary.</param>
public readonly record struct TcpOptions(ReadOnlyMemory<byte> Data)
{
    #region Sentinels

    /// <summary>An empty options value (no TCP options).</summary>
    public static TcpOptions Empty { get; }

    #endregion

    #region Conversions

    /// <summary>Implicitly wraps a byte array as <see cref="TcpOptions"/>.</summary>
    public static implicit operator TcpOptions(byte[] data) => new(data);

    /// <summary>Implicitly wraps a <see cref="ReadOnlyMemory{T}"/> as <see cref="TcpOptions"/>.</summary>
    public static implicit operator TcpOptions(ReadOnlyMemory<byte> data) => new(data);

    #endregion
}
