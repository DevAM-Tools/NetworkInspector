// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetworkInspector.Generators.Models;

namespace NetworkInspector.Generators;

/// <summary>
/// Roslyn incremental source generator for protocol classes annotated with <c>[Protocol]</c>.
/// Generates <c>RegisterFields()</c>, field ID fields, index group fields,
/// protocol table fields, setting registration, and <c>Name</c>/<c>UiName</c> properties.
/// <para>This type is split into the following partials:
/// <list type="bullet">
/// <item><description><see cref="ProtocolGenerator"/> (this file): constants, public API.</description></item>
/// <item><description><c>ProtocolGenerator.Diagnostics.cs</c>: NIGEN001..NIGEN013 diagnostic descriptors.</description></item>
/// <item><description><c>ProtocolGenerator.Extraction.cs</c>: symbol/attribute traversal and validation.</description></item>
/// <item><description><c>ProtocolGenerator.Emit.cs</c>: source-text generation and identifier utilities.</description></item>
/// </list>
/// </para>
/// <para>Thread safety: This class is stateless; all mutable state lives in pipeline DTOs
/// (see <see cref="ProtocolInfo"/>). Multiple instances may run concurrently in one
/// compiler invocation.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class ProtocolGenerator : IIncrementalGenerator
{
    #region Constants

    /// <summary>Fully-qualified name of the Protocol attribute, used with ForAttributeWithMetadataName.</summary>
    private const string _FqnProtocolAttribute = "NetworkInspector.Protocols.Attributes.ProtocolAttribute";

    // Fully-qualified attribute names (namespace + short name) for all relevant attributes.
    // These are compared against attr.AttributeClass?.ToDisplayString() to prevent hijacking
    // by user-defined attributes with the same short name in a different namespace.
    private const string _FqnNs = "NetworkInspector.Protocols.Attributes.";
    private const string _FqnRegisterAtTableAttribute = _FqnNs + "RegisterAtTableAttribute";
    private const string _FqnRegisterAtStringTableAttribute = _FqnNs + "RegisterAtStringTableAttribute";
    private const string _FqnRegisterAtBoolTableAttribute = _FqnNs + "RegisterAtBoolTableAttribute";
    private const string _FqnRegisterAtBytesTableAttribute = _FqnNs + "RegisterAtBytesTableAttribute";
    private const string _FqnRegisterAtAnyTableAttribute = _FqnNs + "RegisterAtAnyTableAttribute";
    private const string _FqnUsesTableAttribute = _FqnNs + "UsesTableAttribute";
    private const string _FqnProtocolTableU64Attribute = _FqnNs + "ProtocolTableU64Attribute";
    private const string _FqnProtocolTableStringAttribute = _FqnNs + "ProtocolTableStringAttribute";
    private const string _FqnProtocolTableBytesAttribute = _FqnNs + "ProtocolTableBytesAttribute";
    private const string _FqnProtocolTableBoolAttribute = _FqnNs + "ProtocolTableBoolAttribute";
    private const string _FqnProtocolTableAnyAttribute = _FqnNs + "ProtocolTableAnyAttribute";
    private const string _FqnBoolSettingAttribute = _FqnNs + "BoolSettingAttribute";
    private const string _FqnStringSettingAttribute = _FqnNs + "StringSettingAttribute";
    private const string _FqnF64SettingAttribute = _FqnNs + "F64SettingAttribute";
    private const string _FqnU64SettingAttribute = _FqnNs + "U64SettingAttribute";
    private const string _FqnI64SettingAttribute = _FqnNs + "I64SettingAttribute";
    private const string _FqnBytesSettingAttribute = _FqnNs + "BytesSettingAttribute";
    private const string _FqnEnumSettingAttribute = _FqnNs + "EnumSettingAttribute";

    // Field attribute suffix — used after confirming the namespace prefix is correct.
    private const string _FieldAttributeSuffix = "FieldAttribute";

    // global::-qualified type names emitted into generated code so the output compiles
    // without relying on the consumer project's global usings.
    private const string _GloIStackBuilder = "global::NetworkInspector.Core.Interfaces.IStackBuilder";
    private const string _GloProtocolId = "global::NetworkInspector.Core.Ids.ProtocolId";
    private const string _GloIndexGroupId = "global::NetworkInspector.Core.Ids.IndexGroupId";
    private const string _GloProtocolTableId = "global::NetworkInspector.Core.Ids.ProtocolTableId";
    private const string _GloFieldType = "global::NetworkInspector.Core.Fields.FieldType";
    private const string _GloTableKeyType = "global::NetworkInspector.Core.Tables.ProtocolTableKeyType";
    private const string _GloBytesKey = "global::NetworkInspector.Core.Tables.BytesKey";
    private const string _GloEnumMetadata = "global::NetworkInspector.Core.Settings.EnumSettingMetadata";
    private const string _GloSettingsRegistrar = "global::NetworkInspector.Core.Settings.SettingsRegistrar";
    private const string _GloStack = "global::NetworkInspector.Core.Stack";
    private const string _GloGeneratedCode = "global::System.CodeDom.Compiler.GeneratedCodeAttribute";
    private const string _GloExcludeFromCoverage = "global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute";

    /// <summary>Version embedded in <c>[GeneratedCode]</c> attributes on each generated partial class.</summary>
    private const string _GeneratorVersion = "1.0.0";

    /// <summary>Diagnostic category shared by every NIGEN descriptor in the Diagnostics partial.</summary>
    private const string _DiagCategory = "NetworkInspector.Generators";

    #endregion

    #region Public API

    /// <summary>
    /// Initializes the incremental generator pipeline.
    /// Uses <see cref="SyntaxValueProvider.ForAttributeWithMetadataName"/> for cache-friendly,
    /// per-symbol detection — the transform runs only when a class bearing <c>[Protocol]</c>
    /// actually changes, eliminating the need to combine with <c>CompilationProvider</c>.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ProtocolInfo?> provider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                _FqnProtocolAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, _) => ExtractProtocolInfo(
                    (INamedTypeSymbol)ctx.TargetSymbol, LocationInfo.From(ctx.TargetNode.GetLocation())))
            .Where(static info => info is not null);

        context.RegisterSourceOutput(provider, static (spc, info) => Execute(spc, info!));
    }

    #endregion
}
