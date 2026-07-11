// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Generators.Tests;

/// <summary>
/// Roslyn <see cref="CSharpGeneratorDriver"/> tests for <see cref="ProtocolGenerator"/>.
/// Each test covers one NIGEN diagnostic (001–013) or the happy-path source-emission contract.
/// Shared driver scaffolding lives in <see cref="TestInfrastructure"/>.
/// </summary>
internal sealed class ProtocolGeneratorTests
{
    #region Happy path — valid protocol produces generated source

    [Test]
    public async Task Run_WhenMinimalValidProtocol_ProducesGeneratedSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test Protocol")]
            public partial class TestProtocol { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);
        bool hasErrorDiagnostics = result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);

        await Assert.That(hasErrorDiagnostics).IsFalse();
        await Assert.That(HasGeneratedSource(result)).IsTrue();
    }

    #endregion

    #region NIGEN001 — duplicate field name

    [Test]
    public async Task Run_WhenDuplicateFieldName_EmitsNigen001AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class DupFieldProto
            {
                [U64Field("dup.name", "Dup A")]
                private int _fieldA;
                [U64Field("dup.name", "Dup B")]
                private int _fieldB;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN001")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN002 — duplicate setting name

    [Test]
    public async Task Run_WhenDuplicateSettingName_EmitsNigen002AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class DupSettingProto
            {
                [BoolSetting("dup.setting", "Setting A", "General")]
                private bool _settingA;
                [BoolSetting("dup.setting", "Setting B", "General")]
                private bool _settingB;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN002")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN003 — duplicate dispatch table name

    [Test]
    public async Task Run_WhenDuplicateDispatchTableName_EmitsNigen003AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class DupTableProto
            {
                [ProtocolTableU64("dup.table", "Table A")]
                private int _tableA;
                [ProtocolTableU64("dup.table", "Table B")]
                private int _tableB;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN003")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN004 — invalid identifier name

    [Test]
    public async Task Run_WhenProtocolNameContainsInvalidChar_EmitsNigen004AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("bad#name", "Test")]
            public partial class InvalidNameProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN004")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    [Test]
    public async Task Run_WhenFieldNameContainsSpace_EmitsNigen004AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class SpaceInFieldProto
            {
                [U64Field("has space", "Field")]
                private int _field;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN004")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN005 — invalid enum pair value

    [Test]
    public async Task Run_WhenEnumPairValueIsNonNumeric_EmitsNigen005AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class EnumProto
            {
                [EnumSetting("enum.mode", "Mode", "General", AllowedValues = "Off=0;On=NotANumber")]
                private ulong _modeSetting;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN005")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN006 — generic protocol class

    [Test]
    public async Task Run_WhenProtocolClassIsGeneric_EmitsNigen006AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class GenericProto<T> { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN006")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN007 — nested protocol class

    [Test]
    public async Task Run_WhenProtocolClassIsNested_EmitsNigen007AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            public class Outer
            {
                [Protocol("test", "Test")]
                public partial class NestedProto { }
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN007")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN008 — protocol class in global namespace

    [Test]
    public async Task Run_WhenProtocolClassIsInGlobalNamespace_EmitsNigen008AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            [Protocol("test", "Test")]
            public partial class GlobalNamespaceProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN008")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN009 — invalid boolean dispatch key

    [Test]
    public async Task Run_WhenBoolTableKeyIsNotBool_EmitsNigen009AndSuppressesSource()
    {
        // Uses the string-key overload of RegisterAtBoolTableAttribute defined in the stubs
        // so the Roslyn semantic model records a string value — the generator then sees
        // keyObj is not bool and emits NIGEN009.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            [RegisterAtBoolTable("bool.table", "notabool")]
            public partial class NonBoolKeyProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN009")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN010 — unknown field attribute (warning: source still emitted)

    [Test]
    public async Task Run_WhenUnknownFieldAttributeIsUsed_EmitsNigen010AndStillProducesSource()
    {
        // UnknownFieldAttribute is in the NetworkInspector.Protocols.Attributes namespace
        // (satisfies the namespace-prefix check) and ends with "FieldAttribute" (satisfies
        // the suffix check), but is absent from the generator's FieldType switch, which
        // triggers NIGEN010. Because NIGEN010 is a warning the source IS still emitted.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class UnknownFieldProto
            {
                [UnknownField("custom.field", "Custom")]
                private int _customField;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN010")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsTrue();
    }

    #endregion

    #region NIGEN011 — invalid DefaultHex in bytes setting

    [Test]
    public async Task Run_WhenBytesDefaultHexContainsInvalidChars_EmitsNigen011AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class BadHexProto
            {
                [BytesSetting("bytes.key", "Bytes", "General", DefaultHex = "ZZZZ")]
                private int _bytesSetting;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN011")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    [Test]
    public async Task Run_WhenBytesDefaultHexHasOddLength_EmitsNigen011AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public partial class OddHexProto
            {
                [BytesSetting("bytes.key", "Bytes", "General", DefaultHex = "ABC")]
                private int _bytesSetting;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN011")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN012 — protocol class must be partial

    [Test]
    public async Task Run_WhenProtocolClassIsNotPartial_EmitsNigen012AndSuppressesSource()
    {
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            public class NonPartialProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN012")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsFalse();
    }

    #endregion

    #region NIGEN013 — attribute payload incomplete (warning: source still emitted)

    [Test]
    public async Task Run_WhenBoolTablePayloadIsIncomplete_EmitsNigen013AndStillProducesSource()
    {
        // Uses the 1-arg overload of RegisterAtBoolTableAttribute defined in the stubs.
        // The generator sees ConstructorArguments.Length == 1 < 2 and emits NIGEN013.
        // Because NIGEN013 is a warning the source IS still emitted.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            [RegisterAtBoolTable("bool.table")]
            public partial class IncompleteBoolTableProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(HasDiagnostic(result, "NIGEN013")).IsTrue();
        await Assert.That(HasGeneratedSource(result)).IsTrue();
    }

    #endregion

    #region RegisterAtAnyTable — valid table registration produces source

    [Test]
    public async Task Run_WhenRegisterAtAnyTableValid_ProducesSource()
    {
        // RegisterAtAnyTable records a "match-any" dispatch registration without a key.
        // Verify no error diagnostics and that source is emitted.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("test", "Test")]
            [RegisterAtAnyTable("any.dispatch.table")]
            public partial class AnyTableProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
        await Assert.That(HasGeneratedSource(result)).IsTrue();
    }

    #endregion

    #region RegisterAtBytesTable — valid byte-array key produces source

    [Test]
    public async Task Run_WhenRegisterAtBytesTableWithByteArray_ProducesSource()
    {
        // RegisterAtBytesTable accepts a byte[] literal — verify extraction and emission succeed.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("bytes.proto", "Bytes Protocol")]
            [RegisterAtBytesTable("bytes.dispatch.table", new byte[] { 0x01, 0x02 })]
            public partial class BytesTableProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
        await Assert.That(HasGeneratedSource(result)).IsTrue();
    }

    #endregion

    #region IndexGroup ordering — generated output is sorted lexicographically

    [Test]
    public async Task Run_WhenMultipleIndexGroupsInUnsortedOrder_GeneratedSourceHasSortedGroups()
    {
        // Fields declared with IndexGroup="ZGroup" first, then "AGroup".
        // The generator must sort index groups lexicographically (Ordinal) so the output is
        // deterministic regardless of field declaration order.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("sort.proto", "Sort Test")]
            public partial class SortedIndexGroupProto
            {
                [U64Field("sort.z", "Z Field", IndexGroup = "ZGroup")]
                private int _zField;
                [U64Field("sort.a", "A Field", IndexGroup = "AGroup")]
                private int _aField;
            }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
        await Assert.That(HasGeneratedSource(result)).IsTrue();

        string generatedSource = GetFirstGeneratedSource(result);
        int indexOfAGroup = generatedSource.IndexOf("AGroup", StringComparison.Ordinal);
        int indexOfZGroup = generatedSource.IndexOf("ZGroup", StringComparison.Ordinal);

        // "AGroup" must appear before "ZGroup" in the generated code
        await Assert.That(indexOfAGroup).IsGreaterThan(-1).Because("AGroup must be in generated source");
        await Assert.That(indexOfZGroup).IsGreaterThan(-1).Because("ZGroup must be in generated source");
        await Assert.That(indexOfAGroup < indexOfZGroup).IsTrue()
            .Because("IndexGroups must be sorted lexicographically (Ordinal)");
    }

    #endregion

    #region Multiple protocol classes — all classes produce generated sources

    [Test]
    public async Task Run_WhenTwoProtocolClasses_BothProduceGeneratedSources()
    {
        // Two protocol classes in the same compilation must each get their own generated partial.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;

            [Protocol("first.proto", "First Protocol")]
            public partial class FirstProto { }

            [Protocol("second.proto", "Second Protocol")]
            public partial class SecondProto { }
            """;

        GeneratorDriverRunResult result = RunGenerator(source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
        // Each protocol class produces one generated source file
        await Assert.That(result.Results[0].GeneratedSources.Length).IsGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Numeric setting extraction and emission roundtrip

    // Extended stubs that add Min/Max (F64) and HasMin/HasMax/Min/Max (U64/I64) properties.
    // These are NOT in the shared AttributeStubs because they are only needed here.
    private const string _NumericSettingStubs = """
        using System;
        namespace NetworkInspector.Protocols.Attributes
        {
            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class ProtocolAttribute : Attribute
            {
                public ProtocolAttribute(string name, string uiName) { }
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class F64SettingAttribute : Attribute
            {
                public F64SettingAttribute(string name, string uiName, string groupName) { }
                public double Default { get; set; }
                public double Min { get; set; }
                public double Max { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class U64SettingAttribute : Attribute
            {
                public U64SettingAttribute(string name, string uiName, string groupName) { }
                public ulong Default { get; set; }
                public bool HasMin { get; set; }
                public ulong Min { get; set; }
                public bool HasMax { get; set; }
                public ulong Max { get; set; }
                public string Description { get; set; } = "";
            }

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class I64SettingAttribute : Attribute
            {
                public I64SettingAttribute(string name, string uiName, string groupName) { }
                public long Default { get; set; }
                public bool HasMin { get; set; }
                public long Min { get; set; }
                public bool HasMax { get; set; }
                public long Max { get; set; }
                public string Description { get; set; } = "";
            }
        }
        """;

    [Test]
    public async Task Run_WhenF64SettingWithDefault_GeneratedSourceContainsInvariantFormattedValue()
    {
        // The generator must emit default values using "R" format with InvariantCulture.
        // Verifies the roundtrip: F64 3.14 → "3.14" in the generated RegisterF64Setting call.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("f64.proto", "F64 Test")]
            public partial class F64DefaultProto
            {
                [F64Setting("f64.key", "F64 Key", "General", Default = 3.14)]
                private double _f64Setting;
            }
            """;

        GeneratorDriverRunResult result = RunGeneratorFromRawSources(_NumericSettingStubs, source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
        await Assert.That(HasGeneratedSource(result)).IsTrue();

        string generatedSource = GetFirstGeneratedSource(result);
        // Default 3.14 must appear in the RegisterF64Setting call
        await Assert.That(generatedSource.Contains("3.14", StringComparison.Ordinal)).IsTrue()
            .Because("F64 default must be emitted using invariant-culture round-trip format");
    }

    [Test]
    public async Task Run_WhenF64SettingWithMinMax_GeneratedSourceContainsMinMaxArgs()
    {
        // When Min and Max are set on an F64Setting, the generator must emit named
        // 'min:' and 'max:' arguments in the RegisterF64Setting call.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("f64.proto", "F64 MinMax Test")]
            public partial class F64MinMaxProto
            {
                [F64Setting("f64.key", "F64 Key", "General", Default = 1.0, Min = 0.1, Max = 9.9)]
                private double _f64Setting;
            }
            """;

        GeneratorDriverRunResult result = RunGeneratorFromRawSources(_NumericSettingStubs, source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();

        string generatedSource = GetFirstGeneratedSource(result);
        await Assert.That(generatedSource.Contains("min:", StringComparison.Ordinal)).IsTrue()
            .Because("F64 min must be emitted when Min is set");
        await Assert.That(generatedSource.Contains("max:", StringComparison.Ordinal)).IsTrue()
            .Because("F64 max must be emitted when Max is set");
    }

    [Test]
    public async Task Run_WhenU64SettingWithHasMinHasMax_GeneratedSourceContainsMinMaxArgs()
    {
        // U64Setting uses HasMin/HasMax flags (not NaN sentinel) to control min/max emission.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("u64.proto", "U64 MinMax Test")]
            public partial class U64MinMaxProto
            {
                [U64Setting("u64.key", "U64 Key", "General",
                    Default = 42, HasMin = true, Min = 5, HasMax = true, Max = 1000)]
                private ulong _u64Setting;
            }
            """;

        GeneratorDriverRunResult result = RunGeneratorFromRawSources(_NumericSettingStubs, source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();

        string generatedSource = GetFirstGeneratedSource(result);
        // Both min and max must appear in the emitted RegisterU64Setting call
        await Assert.That(generatedSource.Contains("42", StringComparison.Ordinal)).IsTrue()
            .Because("U64 default value 42 must be emitted");
        await Assert.That(generatedSource.Contains("min:", StringComparison.Ordinal)).IsTrue()
            .Because("U64 min must be emitted when HasMin=true");
        await Assert.That(generatedSource.Contains("max:", StringComparison.Ordinal)).IsTrue()
            .Because("U64 max must be emitted when HasMax=true");
    }

    [Test]
    public async Task Run_WhenU64SettingHasMinFalse_GeneratedSourceOmitsMinArg()
    {
        // When HasMin=false (default), the min: named argument must NOT appear in the generated code.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("u64.proto", "U64 NoMin Test")]
            public partial class U64NoMinProto
            {
                [U64Setting("u64.nomin", "U64 NoMin", "General", Default = 7)]
                private ulong _u64Setting;
            }
            """;

        GeneratorDriverRunResult result = RunGeneratorFromRawSources(_NumericSettingStubs, source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();

        string generatedSource = GetFirstGeneratedSource(result);
        // min:/max: must NOT appear when flags are false
        await Assert.That(generatedSource.Contains("min:", StringComparison.Ordinal)).IsFalse()
            .Because("min: must not be emitted when HasMin=false");
        await Assert.That(generatedSource.Contains("max:", StringComparison.Ordinal)).IsFalse()
            .Because("max: must not be emitted when HasMax=false");
    }

    [Test]
    public async Task Run_WhenI64SettingWithNegativeMinMax_GeneratedSourceContainsNegativeValues()
    {
        // I64Setting supports negative min/max — verify the sign is preserved in emission.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("i64.proto", "I64 MinMax Test")]
            public partial class I64MinMaxProto
            {
                [I64Setting("i64.key", "I64 Key", "General",
                    Default = -5, HasMin = true, Min = -100, HasMax = true, Max = 100)]
                private long _i64Setting;
            }
            """;

        GeneratorDriverRunResult result = RunGeneratorFromRawSources(_NumericSettingStubs, source);

        await Assert.That(result.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)).IsFalse();

        string generatedSource = GetFirstGeneratedSource(result);
        // Negative default and negative min must appear
        await Assert.That(generatedSource.Contains("-5", StringComparison.Ordinal)).IsTrue()
            .Because("I64 negative default must be preserved in emission");
        await Assert.That(generatedSource.Contains("-100", StringComparison.Ordinal)).IsTrue()
            .Because("I64 negative min must be preserved in emission");
        await Assert.That(generatedSource.Contains("min:", StringComparison.Ordinal)).IsTrue()
            .Because("min: named arg must appear when HasMin=true");
    }

    #endregion

    #region Incremental caching — pipeline reuses cached ProtocolInfo across unrelated edits

    [Test]
    public async Task Generator_WhenUnrelatedSyntaxTreeAdded_ReusesCachedProtocolInfoStep()
    {
        // Incremental-generator contract: an edit that does not touch any [Protocol] type must
        // not re-run the extraction transform. This only holds if ProtocolInfo has correct
        // value equality, so this test also guards the DTO's equatability.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("inc.test", "Incremental Test")]
            public partial class IncrementalProto
            {
                [U64Field("inc.field", "Inc Field")]
                private int _field;
            }
            """;

        Compilation compilation = CreateCompilation(source);
        GeneratorDriver driver = CreateTrackingDriver();

        // First run populates the cache.
        driver = driver.RunGenerators(compilation);

        // Add a syntax tree with no [Protocol] type — an edit unrelated to the generator inputs.
        Compilation modified = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace Unrelated { internal sealed class Bystander { } }"));
        driver = driver.RunGenerators(modified);

        GeneratorRunResult runResult = driver.GetRunResult().Results[0];

        bool extractionCached = runResult.TrackedSteps[ProtocolGenerator.TrackingNames.ProtocolInfo]
            .SelectMany(static step => step.Outputs)
            .All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);

        bool filteredCached = runResult.TrackedSteps[ProtocolGenerator.TrackingNames.FilteredProtocolInfo]
            .SelectMany(static step => step.Outputs)
            .All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);

        await Assert.That(extractionCached).IsTrue()
            .Because("ProtocolInfo extraction must be cached when no [Protocol] type changes");
        await Assert.That(filteredCached).IsTrue()
            .Because("filtered ProtocolInfo step must be cached when no [Protocol] type changes");
    }

    [Test]
    public async Task Generator_WhenRunTwiceOnEquivalentCompilation_ProducesCachedOutputs()
    {
        // Re-running against a structurally identical compilation must reuse every cached step.
        const string source = """
            using NetworkInspector.Protocols.Attributes;
            namespace TestProtocols;
            [Protocol("inc.equal", "Incremental Equal")]
            public partial class IncrementalEqualProto
            {
                [U64Field("inc.equal.field", "Field")]
                private int _field;
            }
            """;

        GeneratorDriver driver = CreateTrackingDriver();
        driver = driver.RunGenerators(CreateCompilation(source));

        // A second, independently-built compilation with identical content.
        driver = driver.RunGenerators(CreateCompilation(source));

        GeneratorRunResult runResult = driver.GetRunResult().Results[0];

        bool extractionCached = runResult.TrackedSteps[ProtocolGenerator.TrackingNames.ProtocolInfo]
            .SelectMany(static step => step.Outputs)
            .All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);

        await Assert.That(extractionCached).IsTrue()
            .Because("equivalent compilations must yield cached extraction outputs");
    }

    #endregion
}
