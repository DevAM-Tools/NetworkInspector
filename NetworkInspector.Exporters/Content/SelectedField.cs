// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>One resolved field of a <see cref="FieldSelection"/>.</summary>
/// <param name="Id">Field id in the selection's stack.</param>
/// <param name="Name">Registered field name.</param>
/// <param name="FieldType">Declared value type; never <see cref="FieldType.None"/> (value-less fields are refused).</param>
/// <param name="Occurrences">Occurrence choice for this field.</param>
public readonly record struct SelectedField(FieldId Id, string Name, FieldType FieldType, Occurrences Occurrences);
