// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Empty dummy protocol that exists solely to be the owning protocol of the root field.
/// Every field in the tree must have an owning protocol — <see cref="RootProtocol"/>
/// fulfils this requirement for the tree root. It performs no parsing.
/// </summary>
internal sealed class RootProtocol : IProtocol
{
    /// <inheritdoc/>
    public string Name => "root";

    /// <inheritdoc/>
    public string UiName => "Root";

    /// <inheritdoc/>
    public string? Description => "Owning protocol for the root field";

    /// <summary>
    /// No-op — <see cref="RootProtocol"/> does not own any fields beyond the root field
    /// (which is registered by <see cref="StackBuilder"/>).
    /// </summary>
    /// <param name="builder">The builder; unused, because the root field is registered by <see cref="StackBuilder"/>.</param>
    /// <param name="protocolId">The id of this protocol; unused.</param>
    internal static void RegisterWith(IStackBuilder builder, ProtocolId protocolId)
    {
        // Root field is registered by StackBuilder. Nothing else to register.
        _ = builder;
        _ = protocolId;
    }

    /// <summary>
    /// <see cref="RootProtocol"/> is never called directly — <see cref="PacketProtocol"/>
    /// is the parse entry point (<see cref="Packet"/> dispatches to the stack's packet protocol, not to the root).
    /// Returns 0 (Ok, nothing consumed) as a safety fallback.
    /// </summary>
    /// <param name="parentField">The parent field; unused.</param>
    /// <param name="data">The packet data; unused.</param>
    /// <param name="context">The parse context; unused.</param>
    /// <returns>0: success with no byte consumed.</returns>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;
}
