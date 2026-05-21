// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Protocols;

/// <summary>
/// Empty dummy protocol that exists solely to be the owning protocol of the root field.
/// Every field in the tree must have an owning protocol — <see cref="RootProtocol"/>
/// fulfils this requirement for the tree root. It performs no parsing.
/// </summary>
internal sealed class RootProtocol : IProtocol
{
    #region Properties

    public string Name => "root";
    public string UiName => "Root";
    public string? Description => "Owning protocol for the root field";

    #endregion

    #region Methods

    /// <summary>
    /// No-op — <see cref="RootProtocol"/> does not own any fields beyond the root field
    /// (which is registered by <see cref="StackBuilder"/>).
    /// </summary>
    internal static void RegisterWith(IStackBuilder builder, ProtocolId protocolId)
    {
        // Root field is registered by StackBuilder. Nothing else to register.
        _ = builder;
        _ = protocolId;
    }

    /// <summary>
    /// <see cref="RootProtocol"/> is never called directly — <see cref="PacketProtocol"/>
    /// is the parse entry point. Returns 0 as a safety fallback.
    /// </summary>
    public ParseResult Parse(in MutField parentField, ReadOnlyMemory<byte> data, in ParseContext context) => 0;

    #endregion
}
