// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Testing.Tshark;

/// <summary>
/// Convenience helpers for tests that depend on an external <c>tshark</c> binary.
/// </summary>
/// <remarks>
/// <para>tshark is mandatory for cross-validation tests by default. When it is missing
/// on <c>PATH</c>, the extraction methods on <see cref="TsharkVerifier"/> throw at the
/// call site so a release/CI run cannot silently lose its evidence.</para>
/// <para>Local developers without Wireshark may set
/// <c>NETWORKINSPECTOR_ALLOW_MISSING_TSHARK=1</c>; that downgrades the missing-tshark
/// error to a silent skip. Tests that opt into the escape hatch can call
/// <see cref="ShouldSkip"/> at the top of the test body and <c>return</c> early to
/// document the deliberate skip.</para>
/// </remarks>
public static class TsharkAvailability
{
    /// <summary>
    /// Returns <see langword="true"/> when <c>tshark</c> is available on the system PATH.
    /// </summary>
    public static bool IsAvailable() => TsharkVerifier.IsAvailable();

    /// <summary>
    /// Returns <see langword="true"/> when the test should silently skip cross-validation
    /// because tshark is missing and the <see cref="TsharkVerifier.AllowMissingEnvVar"/>
    /// developer escape hatch is enabled. CI/release runs leave the variable unset so
    /// this returns <see langword="false"/> and the test must call <see cref="TsharkVerifier"/>
    /// methods which will then throw if tshark is absent.
    /// </summary>
    public static bool ShouldSkip() => !IsAvailable() && TsharkVerifier.MissingTsharkAllowed;
}
