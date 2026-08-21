// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Filter.Errors;

/// <summary>
/// Factory methods for <see cref="FilterResult{T}"/>.
/// <para>
/// The factories live on a separate non-generic type so call sites read
/// <c>FilterResult.Fail&lt;Token&gt;(error)</c> instead of repeating the closed generic type, and
/// so <see cref="FilterResult{T}"/> itself exposes no static surface.
/// </para>
/// </summary>
public static class FilterResult
{
    #region Factories

    /// <summary>Creates a success result.</summary>
    public static FilterResult<T> Ok<T>(T value) => new(value);

    /// <summary>Creates a failure result.</summary>
    public static FilterResult<T> Fail<T>(FilterError error) => new(error);

    #endregion
}
