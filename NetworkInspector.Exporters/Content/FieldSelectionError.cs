// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Exporters;

/// <summary>Why a selection could not be created.</summary>
/// <param name="Code">Error code.</param>
/// <param name="FieldName">The offending name, or empty for <see cref="FieldSelectionErrorCode.Empty"/>.</param>
/// <param name="Message">Message for users; names the field.</param>
public readonly record struct FieldSelectionError(FieldSelectionErrorCode Code, string FieldName, string Message);
