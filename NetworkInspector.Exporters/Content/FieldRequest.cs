// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>One requested field by name, optionally with its own occurrence choice (otherwise the selection default).</summary>
/// <param name="Name">Field name as registered in the stack, for example <c>ip.src</c>.</param>
/// <param name="Occurrences">Occurrence choice for this field; <see langword="null"/> uses the selection default.</param>
public readonly record struct FieldRequest(string Name, Occurrences? Occurrences = null);
