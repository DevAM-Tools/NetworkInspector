// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Settings;

/// <summary>
/// Throw helpers of the settings. A throw expression and the construction of its exception add code to the method that holds
/// them; putting the throw into a <see cref="MethodImplOptions.NoInlining"/> method keeps the guarded method small and
/// places the cold code out of line. Stateless and thread-safe.
/// </summary>
internal static class SettingThrowHelpers
{
    /// <summary>
    /// Throws when a non-finite F64 setting value is encountered during JSON serialization. JSON has no NaN or infinity, and
    /// settings are validated to be finite, so reaching this is a broken invariant (a bug), not an expected failure.
    /// </summary>
    /// <param name="value">The value that is not finite.</param>
    /// <exception cref="InvalidOperationException">Always; the message names the value in invariant culture.</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ThrowNonFiniteF64(double value)
        => throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"F64 setting value must be finite, got {value}."));
}
