// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Plugins.Tests;

/// <summary>
/// Compiles small C# sources into assemblies with Roslyn, so that tests can build exactly the plugin shapes they need (entry
/// types that list or do not list the interface, dummy higher-tier contracts, private dependencies) without a project per
/// shape. The references are the runtime's trusted platform assemblies plus the contract and Core assemblies of the test
/// process. Stateless; thread-safe.
/// </summary>
internal static class TestAssemblyCompiler
{
    #region Public API

    /// <summary>Compiles <paramref name="source"/> and writes the result to <paramref name="path"/>.</summary>
    /// <param name="path">Output file; its folder is created when missing.</param>
    /// <param name="assemblyName">Assembly name.</param>
    /// <param name="source">C# source text (language version 12).</param>
    /// <param name="extraReferences">Paths of additional assemblies the source references.</param>
    /// <param name="kind">Output kind; a net module has metadata but no assembly manifest.</param>
    /// <returns>The output path.</returns>
    /// <exception cref="InvalidOperationException">The source does not compile; the message lists the diagnostics.</exception>
    internal static string Compile(string path, string assemblyName, string source, IReadOnlyList<string>? extraReferences = null,
        OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp12));
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, [tree], _References(extraReferences),
            new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable));

        EmitResult result;
        using (FileStream stream = File.Create(path))
        {
            result = compilation.Emit(stream);
        }

        if (!result.Success)
        {
            File.Delete(path);
            throw new InvalidOperationException(
                "Test assembly " + assemblyName + " does not compile:\n"
                + string.Join('\n', result.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error)));
        }

        return path;
    }

    #endregion

    #region Private helpers

    // Framework facades plus the assemblies a plugin source needs; computed per call (compilation is rare and cold).
    private static List<MetadataReference> _References(IReadOnlyList<string>? extraReferences)
    {
        List<MetadataReference> references = [];
        string trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (string file in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("System.", StringComparison.Ordinal) || name is "System" or "netstandard" or "mscorlib")
            {
                references.Add(MetadataReference.CreateFromFile(file));
            }
        }

        references.Add(MetadataReference.CreateFromFile(typeof(IPlugin).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(StackBuilder).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Timestamp).Assembly.Location));
        foreach (string extra in extraReferences ?? [])
        {
            references.Add(MetadataReference.CreateFromFile(extra));
        }

        return references;
    }

    #endregion
}
