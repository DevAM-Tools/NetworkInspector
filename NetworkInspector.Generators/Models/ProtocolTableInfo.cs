// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Metadata for a protocol dispatch table extracted from a <c>[ProtocolTable*]</c>-annotated member.
/// </summary>
internal readonly record struct ProtocolTableInfo(
    string FieldName,
    string Name,
    string UiName,
    string KeyType,
    string? Description);
