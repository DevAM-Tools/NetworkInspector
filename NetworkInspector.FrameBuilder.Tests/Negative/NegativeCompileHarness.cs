// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

using System.IO;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace NetworkInspector.FrameBuilder.Tests.Negative;

/// <summary>
/// In-process Roslyn compile harness used by the V0 negative-compile tests.
/// Each snippet is an embedded resource that is wrapped into a tiny C# program
/// referencing the FrameBuilder assembly; the harness returns the resulting
/// diagnostics so the test can assert that compilation produced at least one
/// error.
/// </summary>
/// <remarks>
/// Thread-safety: stateless. The cached <see cref="MetadataReference"/>
/// list is built once and read concurrently — safe because the underlying
/// Roslyn references are immutable.
/// </remarks>
internal static class NegativeCompileHarness
{
    /// <summary>Common preamble injected before every snippet.</summary>
    private const string SnippetPreamble =
        "using System;\n" +
        "using NetworkInspector.FrameBuilder;\n" +
        "using NetworkInspector.FrameBuilder.Constants;\n" +
        "using NetworkInspector.Values;\n" +
        "internal static class _NegativeProgram\n" +
        "{\n" +
        "    private static readonly MacAddress _Dst = MacAddress.FromBytes(new byte[]{1,2,3,4,5,6});\n" +
        "    private static readonly MacAddress _Src = MacAddress.FromBytes(new byte[]{7,8,9,10,11,12});\n" +
        "    private static readonly IPv4Address _SrcIp = new IPv4Address(0x0A000001);\n" +
        "    private static readonly IPv4Address _DstIp = new IPv4Address(0x0A000002);\n" +
        "    internal static void Run()\n" +
        "    {\n";

    /// <summary>Common epilogue closing the wrapper class and method.</summary>
    private const string SnippetEpilogue = "    }\n}\n";

    /// <summary>References used for every snippet compilation.</summary>
    private static readonly MetadataReference[] _References = BuildReferences();

    /// <summary>Loads the snippet resource and returns Roslyn diagnostics.</summary>
    internal static IReadOnlyList<Diagnostic> Compile(string snippetResourceName)
    {
        string body = LoadSnippet(snippetResourceName);
        string source = SnippetPreamble + body + SnippetEpilogue;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilationOptions options = new(
            outputKind: OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true);

        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName: "NetworkInspector.FrameBuilder.NegativeSnippets",
            syntaxTrees: [tree],
            references: _References,
            options: options);

        // Emit to a discardable stream so we observe the *full* diagnostic
        // surface (compilation.GetDiagnostics() alone misses some emit-time
        // checks; for our generic-constraint cases either path suffices).
        using MemoryStream stream = new();
        EmitResult result = compilation.Emit(stream);
        return result.Diagnostics;
    }

    /// <summary>Reads an embedded snippet resource by simple file name.</summary>
    private static string LoadSnippet(string snippetResourceName)
    {
        Assembly asm = typeof(NegativeCompileHarness).Assembly;
        string fullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(snippetResourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded snippet '{snippetResourceName}' not found in {asm.FullName}.");
        using Stream s = asm.GetManifestResourceStream(fullName)!;
        using StreamReader reader = new(s);
        return reader.ReadToEnd();
    }

    /// <summary>Builds the metadata reference set: BCL + FrameBuilder + Values.</summary>
    private static MetadataReference[] BuildReferences()
    {
        // Pull in everything currently loaded in the test process; this gives
        // us all transitive BCL assemblies the snippet needs without having
        // to enumerate them by hand.
        List<MetadataReference> refs = [];
        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (loaded.IsDynamic)
            {
                continue;
            }
            // IL3000: tests do not run as a single-file app; suppressing the warning is safe.
#pragma warning disable IL3000
            string? location = loaded.Location;
#pragma warning restore IL3000
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                continue;
            }
            refs.Add(MetadataReference.CreateFromFile(location));
        }
        return [.. refs];
    }
}
