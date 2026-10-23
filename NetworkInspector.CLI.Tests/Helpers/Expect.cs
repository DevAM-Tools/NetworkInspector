// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.CLI.Tests;

/// <summary>Null checks for tests that need a non-null value without the null-forgiving operator.</summary>
internal static class Expect
{
    /// <summary>Returns the reference value, or throws when it is <see langword="null"/>.</summary>
    /// <param name="value">The value.</param>
    /// <typeparam name="T">A reference type.</typeparam>
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
}
