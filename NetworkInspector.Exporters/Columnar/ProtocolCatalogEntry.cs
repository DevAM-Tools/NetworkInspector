// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters.Columnar;

/// <summary>
/// One row of the <c>protocols</c> catalog written by the columnar analytics sinks (Parquet, DuckDB). Maps the
/// <c>protocol_id</c> used in the <c>fields</c> catalog to the protocol's machine name and display name, so a consumer
/// can label protocols from the exported tables alone.
/// <para>
/// <see cref="ProtocolIdValue"/> is the bare Core <c>ProtocolId.Value</c>, matching
/// <see cref="FieldCatalogEntry.ProtocolIdValue"/>.
/// </para>
/// </summary>
/// <param name="ProtocolIdValue">The registered protocol identifier.</param>
/// <param name="Name">Machine-readable protocol name (e.g. "ip").</param>
/// <param name="UiName">Human-readable display name (e.g. "Internet Protocol").</param>
internal sealed record ProtocolCatalogEntry(int ProtocolIdValue, string Name, string UiName)
{
    #region Factory

    /// <summary>
    /// Builds one entry per protocol registered in <paramref name="stack"/>, in protocol-id order. Called once per export
    /// (setup, not a hot path), so the array allocation is acceptable.
    /// </summary>
    /// <param name="stack">The protocol stack that parsed the exported packets.</param>
    /// <returns>All registered protocols of <paramref name="stack"/>; empty when the stack has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stack"/> is <see langword="null"/>.</exception>
    internal static ProtocolCatalogEntry[] FromStack(Stack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);

        ReadOnlySpan<ProtocolInfo> protocols = stack.Protocols.Span;
        ProtocolCatalogEntry[] entries = new ProtocolCatalogEntry[protocols.Length];
        for (int i = 0; i < protocols.Length; i++)
        {
            ProtocolInfo info = protocols[i];
            entries[i] = new(info.Id.Value, info.Name, info.UiName);
        }
        return entries;
    }

    #endregion
}
