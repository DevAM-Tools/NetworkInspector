// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered index group.
/// Index groups allow fields that always appear together to share a single
/// presence bitmap in the cross-packet index.
/// </summary>
public sealed record IndexGroupInfo(IndexGroupId Id, string Name);
