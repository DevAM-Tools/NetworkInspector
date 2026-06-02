// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Tests;

/// <summary>
/// Shared Roslyn driver scaffolding for the <see cref="ProtocolGenerator"/> test suites:
/// the attribute stubs every test compiles against, the trusted-platform reference set,
/// and helpers that build compilations and run the generator (with or without incremental
/// step tracking). Centralised here so multiple test classes reuse one source of truth.
/// </summary>
internal static class TestInfrastructure
{
    // Minimal stub definitions for every attribute consumed by ProtocolGenerator.
    // They live in the exact FQN namespace the generator uses so that
    // ForAttributeWithMetadataName and fqn-equality checks both fire correctly.
    //
    // RegisterAtBoolTableAttribute intentionally exposes extra overloads:
    //   (string, string)  — used by the NIGEN009 test to supply a non-bool key value.
    //   (string)          — used by the NIGEN013 test to supply an incomplete payload.
    //
    // UnknownFieldAttribute is a field attribute whose short name is absent from the
    // generator's switch expression, deliberately triggering NIGEN010 (warning).
    public const string AttributeStubs = """
        using System;
        namespace NetworkInspector.Protocols.Attributes
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class ProtocolAttribute : Attribute
            {
                public ProtocolAttribute(string name, string uiName) { }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class RegisterAtTableAttribute : Attribute
            {
                public RegisterAtTableAttribute(string table, ulong key) { }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class RegisterAtStringTableAttribute : Attribute
            {
                public RegisterAtStringTableAttribute(string table, string key) { }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class RegisterAtBoolTableAttribute : Attribute
            {
                public RegisterAtBoolTableAttribute(string table, bool key) { }
                public RegisterAtBoolTableAttribute(string table, string key) { }
                public RegisterAtBoolTableAttribute(string table) { }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class RegisterAtBytesTableAttribute : Attribute
            {
                public RegisterAtBytesTableAttribute(string table, byte[] key) { }
            }

            [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class RegisterAtAnyTableAttribute : Attribute
            {
                public RegisterAtAnyTableAttribute(string table) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class UsesTableAttribute : Attribute
            {
                public UsesTableAttribute(string table) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ProtocolTableU64Attribute : Attribute
            {
                public ProtocolTableU64Attribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ProtocolTableStringAttribute : Attribute
            {
                public ProtocolTableStringAttribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ProtocolTableBytesAttribute : Attribute
            {
                public ProtocolTableBytesAttribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ProtocolTableBoolAttribute : Attribute
            {
                public ProtocolTableBoolAttribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class ProtocolTableAnyAttribute : Attribute
            {
                public ProtocolTableAnyAttribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class BoolSettingAttribute : Attribute
            {
                public BoolSettingAttribute(string name, string uiName, string groupName) { }
                public bool Default { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class StringSettingAttribute : Attribute
            {
                public StringSettingAttribute(string name, string uiName, string groupName) { }
                public string Default { get; set; } = "";
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class F64SettingAttribute : Attribute
            {
                public F64SettingAttribute(string name, string uiName, string groupName) { }
                public double Default { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class U64SettingAttribute : Attribute
            {
                public U64SettingAttribute(string name, string uiName, string groupName) { }
                public ulong Default { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class I64SettingAttribute : Attribute
            {
                public I64SettingAttribute(string name, string uiName, string groupName) { }
                public long Default { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class BytesSettingAttribute : Attribute
            {
                public BytesSettingAttribute(string name, string uiName, string groupName) { }
                public string DefaultHex { get; set; } = "";
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class EnumSettingAttribute : Attribute
            {
                public EnumSettingAttribute(string name, string uiName, string groupName) { }
                public ulong Default { get; set; }
                public string AllowedValues { get; set; } = "";
                public string Description { get; set; } = "";
            }

            public abstract class FieldRegistrationAttribute : Attribute
            {
                protected FieldRegistrationAttribute(string name, string uiName) { }
                public string IndexGroup { get; set; } = "";
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class NoneFieldAttribute : FieldRegistrationAttribute
            {
                public NoneFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class U64FieldAttribute : FieldRegistrationAttribute
            {
                public U64FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class I64FieldAttribute : FieldRegistrationAttribute
            {
                public I64FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class F64FieldAttribute : FieldRegistrationAttribute
            {
                public F64FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class StringFieldAttribute : FieldRegistrationAttribute
            {
                public StringFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class BytesFieldAttribute : FieldRegistrationAttribute
            {
                public BytesFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class BoolFieldAttribute : FieldRegistrationAttribute
            {
                public BoolFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class TimestampFieldAttribute : FieldRegistrationAttribute
            {
                public TimestampFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class MacFieldAttribute : FieldRegistrationAttribute
            {
                public MacFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class IPv4FieldAttribute : FieldRegistrationAttribute
            {
                public IPv4FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class IPv6FieldAttribute : FieldRegistrationAttribute
            {
                public IPv6FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class Eui64FieldAttribute : FieldRegistrationAttribute
            {
                public Eui64FieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class UuidFieldAttribute : FieldRegistrationAttribute
            {
                public UuidFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }

            // Not in the generator's known attribute switch — triggers NIGEN010 (warning).
            [AttributeUsage(AttributeTargets.Field)]
            public sealed class UnknownFieldAttribute : FieldRegistrationAttribute
            {
                public UnknownFieldAttribute(string name, string uiName) : base(name, uiName) { }
            }
        }
        """;

    // References built once per test run from the trusted platform assembly list.
    private static readonly MetadataReference[] DefaultReferences = BuildDefaultReferences();

    private static MetadataReference[] BuildDefaultReferences()
    {
        string? platformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(platformAssemblies))
        {
            return [];
        }

        string[] paths = platformAssemblies.Split(Path.PathSeparator);
        MetadataReference[] refs = new MetadataReference[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            refs[i] = MetadataReference.CreateFromFile(paths[i]);
        }

        return refs;
    }

    /// <summary>
    /// Builds a <see cref="CSharpCompilation"/> from the shared attribute stubs and the
    /// caller-supplied test source. Used both for one-shot runs and incremental comparisons.
    /// </summary>
    public static CSharpCompilation CreateCompilation(string source)
    {
        SyntaxTree[] trees =
        [
            CSharpSyntaxTree.ParseText(AttributeStubs),
            CSharpSyntaxTree.ParseText(source),
        ];

        return CSharpCompilation.Create(
            "TestAssembly",
            trees,
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Combines the attribute stubs and the caller-supplied test source into a single
    /// compilation, runs <see cref="ProtocolGenerator"/> against it, and returns the driver result.
    /// </summary>
    public static GeneratorDriverRunResult RunGenerator(string source)
    {
        CSharpCompilation compilation = CreateCompilation(source);
        ProtocolGenerator generator = new();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return driver.GetRunResult();
    }

    /// <summary>
    /// Builds a compilation from raw source strings (without the shared <see cref="AttributeStubs"/>)
    /// and runs <see cref="ProtocolGenerator"/> against it. Used by tests that supply their own stubs.
    /// </summary>
    public static GeneratorDriverRunResult RunGeneratorFromRawSources(params string[] sources)
    {
        SyntaxTree[] trees = sources.Select(static s => CSharpSyntaxTree.ParseText(s)).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            trees,
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ProtocolGenerator generator = new();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return driver.GetRunResult();
    }

    /// <summary>
    /// Creates a generator driver with incremental step tracking enabled so callers can inspect
    /// <see cref="GeneratorRunResult.TrackedSteps"/> after running against successive compilations.
    /// </summary>
    public static GeneratorDriver CreateTrackingDriver()
    {
        ProtocolGenerator generator = new();
        return CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
    }

    public static bool HasDiagnostic(GeneratorDriverRunResult result, string id) =>
        result.Diagnostics.Any(d => d.Id == id);

    public static bool HasGeneratedSource(GeneratorDriverRunResult result) =>
        result.Results.Length > 0 && result.Results[0].GeneratedSources.Length > 0;

    public static string GetFirstGeneratedSource(GeneratorDriverRunResult result) =>
        result.Results.Length > 0 && result.Results[0].GeneratedSources.Length > 0
            ? result.Results[0].GeneratedSources[0].SourceText.ToString()
            : string.Empty;
}
