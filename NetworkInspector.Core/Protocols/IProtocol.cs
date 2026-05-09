// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Delegate for protocol parse methods.
/// Stored in dispatch caches and on <see cref="Stack"/> to bypass interface
/// vtable dispatch. The target method pointer is resolved at delegate
/// creation time, so each invocation is a direct call.
/// </summary>
public delegate ParseResult ParseDelegate(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context);

/// <summary>
/// Contract for protocol parsers.
/// Implementations parse a specific protocol's header from raw packet data.
/// </summary>
public interface IProtocol
{
    #region Properties

    /// <summary>Machine-readable protocol name (e.g., "eth", "ip", "tcp").</summary>
    string Name
    {
        get;
    }

    /// <summary>Human-readable display name (e.g., "Ethernet", "Internet Protocol").</summary>
    string UiName
    {
        get;
    }

    /// <summary>Optional description.</summary>
    string? Description => null;

    #endregion

    #region Methods

    /// <summary>
    /// Called after all protocols are registered and the stack is built.
    /// Exceptions thrown here are caught by <see cref="StackBuilder.Build"/>, collected on
    /// <see cref="IStack.BuildDiagnostics"/>, and do not prevent later protocols from starting.
    /// Callers are expected to inspect these startup errors after build.
    /// </summary>
    void OnStart(Stack stack)
    {
    }

    /// <summary>
    /// Called on session shutdown for cleanup.
    /// This may be invoked even if <see cref="OnStart(Stack)"/> previously threw for the same
    /// protocol and the startup exception was only recorded on the stack.
    /// </summary>
    void OnShutdown(Stack stack)
    {
    }

    /// <summary>
    /// Parse packet data starting at the given position.
    /// Returns bytes consumed on success, or a parse error.
    /// </summary>
    /// <param name="parentField">The parent field to append parsed fields to.</param>
    /// <param name="data">The raw packet data to parse.</param>
    /// <param name="context">The parse context carrying the stack, optional index, and dispatch info.</param>
    /// <returns>A <see cref="ParseResult"/> encoding consumed byte count or error.</returns>
    ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context);

    #endregion
}
