// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter;

/// <summary>
/// Selects <see cref="Filter.Compile(string, IStack?, in FilterObserverOptions)"/> and carries the
/// same compile inputs the field-tree compile accepts.
/// </summary>
/// <remarks>
/// A readonly struct so the overload is distinct from <see cref="FilterCompileOptions"/>.
/// <see cref="Default"/> uses the standard regex timeout and no callbacks.
/// <para><b>Thread-safety:</b> a readonly struct. Safe to share.</para>
/// </remarks>
public readonly struct FilterObserverOptions
{
    #region Properties

    /// <summary>Parse and regex inputs. Null uses the same defaults as a field-tree compile.</summary>
    public FilterCompileOptions? Compile { get; init; }

    /// <summary>Observer compile with default parse inputs.</summary>
    public static FilterObserverOptions Default => default;

    #endregion
}
