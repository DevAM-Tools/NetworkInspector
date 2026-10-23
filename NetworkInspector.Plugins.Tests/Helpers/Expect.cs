// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Turns a nullable value that a test knows is present into a non-null value, failing the test with a clear message instead of
/// a null reference. It replaces the null-forgiving operator in tests. Stateless; thread-safe.
/// </summary>
internal static class Expect
{
    #region Public API

    /// <summary>Returns <paramref name="value"/> or fails.</summary>
    /// <typeparam name="T">A reference type.</typeparam>
    /// <param name="value">The value the test expects to be present.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The value is <see langword="null"/>.</exception>
    internal static T NotNull<T>(T? value)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a value but got null.");
        }

        return value;
    }

    /// <summary>Returns the value of <paramref name="value"/> or fails.</summary>
    /// <typeparam name="T">A value type.</typeparam>
    /// <param name="value">The nullable value the test expects to be present.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The value is <see langword="null"/>.</exception>
    internal static T NotNull<T>(T? value)
        where T : struct
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a value but got null.");
        }

        return value.Value;
    }

    #endregion
}
