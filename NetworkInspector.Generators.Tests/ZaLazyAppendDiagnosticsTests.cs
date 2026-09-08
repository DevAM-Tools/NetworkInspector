// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Tests;

/// <summary>NIGEN015: <c>ZA.Lazy</c> as last argument of MutField custom-text append.</summary>
internal sealed class ZaLazyAppendDiagnosticsTests
{
    #region NIGEN015

    [Test]
    public async Task Run_WhenZaLazyLastArg_EmitsNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m, FieldId id, FieldValue v)
                {
                    m.AppendWithCustomText(id, v, ZA.Lazy("x", 1));
                }
            }
            """;

        GeneratorDriverRunResult result = RunZaLazyDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsTrue();
    }

    [Test]
    public async Task Run_WhenGenericFragments_EmitsNoNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m, FieldId id, FieldValue v)
                {
                    m.AppendWithCustomText(id, v, "x", 1);
                }
            }
            """;

        GeneratorDriverRunResult result = RunZaLazyDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsFalse();
    }

    [Test]
    public async Task Run_WhenSetPacketInfoZaLazy_EmitsNoNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m)
                {
                    m.SetPacketInfo(ZA.Lazy("x", 1));
                }
            }
            """;

        GeneratorDriverRunResult result = RunZaLazyDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsFalse();
    }

    #endregion
}
