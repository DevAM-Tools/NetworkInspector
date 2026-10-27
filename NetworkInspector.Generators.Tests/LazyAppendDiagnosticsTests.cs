// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Tests;

/// <summary>NIGEN015: a prebuilt <c>LazyString</c> as last argument of a MutField custom-text append.</summary>
internal sealed class LazyAppendDiagnosticsTests
{
    #region NIGEN015

    [Test]
    public async Task FormatLazyAsLastArgumentEmitsNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m, FieldId id, FieldValue v)
                {
                    m.AppendWithCustomText(id, v, LazyString.FormatLazy(1, static x => ""));
                }
            }
            """;

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsTrue();
    }

    [Test]
    public async Task LazyAsLastArgumentEmitsNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m, FieldId id, FieldValue v)
                {
                    m.AppendWithCustomText(id, v, LazyString.Lazy(() => ""));
                }
            }
            """;

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsTrue();
    }

    [Test]
    public async Task QualifiedFormatLazyAsLastArgumentEmitsNigen015()
    {
        const string source = """
            class C
            {
                void M(NetworkInspector.Core.Fields.MutField m, NetworkInspector.Core.Fields.FieldId id,
                    NetworkInspector.Core.Fields.FieldValue v)
                {
                    m.AppendWithCustomText(id, v, NetworkInspector.Core.Fields.LazyString.FormatLazy(1, static x => ""));
                }
            }
            """;

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsTrue();
    }

    [Test]
    public async Task PlainFragmentsEmitNoNigen015()
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

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsFalse();
    }

    [Test]
    public async Task LazyTextPassedToSetPacketInfoEmitsNoNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m)
                {
                    m.SetPacketInfo(LazyString.Lazy(() => ""));
                }
            }
            """;

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsFalse();
    }

    [Test]
    public async Task PlainLazyStringAsLastArgumentEmitsNoNigen015()
    {
        const string source = """
            using NetworkInspector.Core.Fields;
            class C
            {
                void M(MutField m, FieldId id, FieldValue v)
                {
                    m.AppendWithCustomText(id, v, new LazyString("x"));
                }
            }
            """;

        GeneratorDriverRunResult result = RunLazyAppendDiagnostics(source);
        await Assert.That(HasDiagnostic(result, "NIGEN015")).IsFalse();
    }

    #endregion
}
