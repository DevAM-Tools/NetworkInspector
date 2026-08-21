// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>Metadata for a registered protocol.</summary>
public sealed record ProtocolInfo(ProtocolId Id, string Name, string UiName, string? Description);
