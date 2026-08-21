// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Models;

/// <summary>
/// Represents an external table reference extracted from <c>[UsesTable("table.name")]</c>.
/// Generates a <c>WhenProtocolTableRegistered</c> call to cache the external table's ID.
/// </summary>
internal readonly record struct UsesTableInfo(string FieldName, string TableName);
