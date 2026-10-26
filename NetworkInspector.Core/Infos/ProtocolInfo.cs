// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol. Immutable record; safe to share between threads.</summary>
/// <param name="Id">Unique protocol identifier; also the position of the protocol's bitmap in the packet index.</param>
/// <param name="Name">Machine-readable protocol name (validated by <see cref="NameValidation.IsValidName"/>).</param>
/// <param name="UiName">Human-readable display name.</param>
/// <param name="Description">Optional description text.</param>
public sealed record ProtocolInfo(ProtocolId Id, string Name, string UiName, string? Description);
