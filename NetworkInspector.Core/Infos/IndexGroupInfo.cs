// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Core.Infos;

/// <summary>
/// Metadata for a registered index group.
/// Index groups allow fields that always appear together to share a single
/// presence bitmap in the cross-packet index. Immutable record; safe to share between threads.
/// </summary>
/// <param name="Id">Dense index group identifier (0 .. count - 1); also the position of the group's bitmap in the packet index.</param>
/// <param name="Name">Name under which the group was registered; groups are shared by name.</param>
public sealed record IndexGroupInfo(IndexGroupId Id, string Name);
