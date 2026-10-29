// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using SomeIpLimitDefinition = NetworkInspector.Protocols.SomeIp.SomeIpLimitDefinition;
using SomeIpLimits = NetworkInspector.Protocols.SomeIp.SomeIpLimits;

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the SOME/IP configuration loader: the DTO deserialization of the shipped examples, the strictness against
/// unknown structure, the schema version check, and the size limits of the file.
/// </summary>
internal sealed class SomeIpConfigLoaderTests
{
    private static SomeIpConfig? _Load(Stream stream, List<SettingsLoadWarning> warnings) =>
        SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings)
            ? config
            : null;

    private static SomeIpConfig? _LoadText(string json, List<SettingsLoadWarning> warnings)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        return _Load(stream, warnings);
    }

    private static string _ConfigWithServices(string version, int serviceCount, int typesPerService = 0)
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{{ \"schema_version\": \"{version}\", \"services\": [");
        for (int i = 0; i < serviceCount; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(CultureInfo.InvariantCulture, $"{{ \"service_id\": {1000 + i}, \"major_version\": 1, \"name\": \"S{i}\", \"data_types\": [");
            for (int t = 0; t < typesPerService; t++)
            {
                if (t > 0)
                {
                    text.Append(',');
                }

                text.Append(CultureInfo.InvariantCulture, $"{{ \"category\": \"alias\", \"name\": \"t{t}\", \"type_reference\": \"uint8\" }}");
            }

            text.Append("] }");
        }

        text.Append("] }");
        return text.ToString();
    }

    #region Shipped examples and strict structure

    [Test]
    [Arguments("minimal_service.json")]
    [Arguments("full_service.json")]
    [Arguments("arrays_and_e2e08.json")]
    [Arguments("unions_bitfields_strings.json")]
    public async Task ShippedExampleDeserializesWithoutAWarning(string fileName)
    {
        List<SettingsLoadWarning> warnings = [];
        using FileStream stream = ShippedConfigFixture.ReadExample("someip", fileName);

        SomeIpConfig? config = _Load(stream, warnings);

        await Assert.That(config).IsNotNull();
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(config!.Services.Length).IsGreaterThanOrEqualTo(1);
        await Assert.That(SomeIpConfigLoader.TryAccept(config, 10_000, 1_000_000, warnings)).IsTrue();
    }

    [Test]
    public async Task ExamplesTogetherKeepTheShapeOfEveryMemberKindAndCategory()
    {
        List<SettingsLoadWarning> warnings = [];
        List<SomeIpConfig> configs = [];
        foreach (string name in new[] { "minimal_service.json", "full_service.json", "arrays_and_e2e08.json", "unions_bitfields_strings.json" })
        {
            using FileStream stream = ShippedConfigFixture.ReadExample("someip", name);
            configs.Add(_Load(stream, warnings)!);
        }

        List<SomeIpServiceConfig> services = [.. configs.SelectMany(static c => c.Services)];
        HashSet<Type> memberKinds = [.. services.SelectMany(static s => s.Methods).Select(static m => m.GetType())];
        HashSet<Type> categories = [.. services.SelectMany(static s => s.DataTypes).Select(static t => t.GetType())];
        List<SomeIpMethodConfig> methods = [.. services.SelectMany(static s => s.Methods).OfType<SomeIpMethodConfig>()];

        await Assert.That(memberKinds.Count).IsEqualTo(4);
        await Assert.That(categories.Count).IsEqualTo(7);
        await Assert.That(methods.Any(static m => m.In.Tlv is not null || m.Out.Tlv is not null)).IsTrue();
        await Assert.That(methods.Any(static m => m.In.Sequential is not null || m.Out.Sequential is not null)).IsTrue();
        await Assert.That(configs.Any(static c => c.Clients is { Count: > 0 })).IsTrue();
    }

    [Test]
    public async Task UnknownCategoryAndUnknownPropertyAreEachOneWarningThatNamesTheNode()
    {
        List<SettingsLoadWarning> category = [];
        List<SettingsLoadWarning> property = [];
        using FileStream categoryStream = ShippedConfigFixture.ReadNegative("someip", "unknown_category.json");
        using FileStream propertyStream = ShippedConfigFixture.ReadNegative("someip", "unknown_property.json");

        SomeIpConfig? categoryConfig = _Load(categoryStream, category);
        SomeIpConfig? propertyConfig = _Load(propertyStream, property);

        await Assert.That(categoryConfig).IsNull();
        await Assert.That(propertyConfig).IsNull();
        await Assert.That(category.Count).IsEqualTo(1);
        await Assert.That(property.Count).IsEqualTo(1);
        await Assert.That(category[0].Message.Contains("enum", StringComparison.Ordinal)).IsTrue();
        await Assert.That(property[0].Message.Contains("colour", StringComparison.Ordinal)).IsTrue();
        await Assert.That(category[0].GroupName).IsEqualTo("someip");
        await Assert.That(category[0].SettingName).IsEqualTo("someip.config_file");
    }

    [Test]
    public async Task UnknownPrimitiveIsNotALoaderDefect()
    {
        // The wire primitive names are checked by the compiler, which names the node; the loader keeps the text.
        List<SettingsLoadWarning> warnings = [];
        using FileStream stream = ShippedConfigFixture.ReadNegative("someip", "unknown_primitive.json");

        SomeIpConfig? config = _Load(stream, warnings);

        await Assert.That(config).IsNotNull();
        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TruncatedAndNotJsonDocumentsAreOneWarningEach()
    {
        List<SettingsLoadWarning> truncated = [];
        List<SettingsLoadWarning> garbage = [];

        SomeIpConfig? first = _LoadText("{\"schema_version\": \"1.0\", \"services\": [", truncated);
        SomeIpConfig? second = _LoadText("not json", garbage);

        await Assert.That(first).IsNull();
        await Assert.That(second).IsNull();
        await Assert.That(truncated.Count).IsEqualTo(1);
        await Assert.That(garbage.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParameterListShapeSelectsTheEncodingAndASiteIsAStringOrAnObject()
    {
        const string Json = """
            { "schema_version": "1.0", "services": [ { "service_id": 1, "major_version": 1, "name": "S", "methods": [
              { "kind": "method", "id": 1, "name": "A", "in": [ "uint8", { "name": "x", "filter_name": "a.b", "ui_name": "X", "type_reference": "uint16", "pad_to": 4 } ], "out": { "1": "uint8", "2": { "type_reference": "uint16" } } }
            ] } ] }
            """;
        List<SettingsLoadWarning> warnings = [];

        SomeIpConfig? config = _LoadText(Json, warnings);

        SomeIpMethodConfig method = (SomeIpMethodConfig)config!.Services[0].Methods[0];
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(method.In.Sequential!.Length).IsEqualTo(2);
        await Assert.That(method.In.Tlv).IsNull();
        await Assert.That(method.In.Sequential[0].TypeReference).IsEqualTo("uint8");
        await Assert.That(method.In.Sequential[1].FilterName).IsEqualTo("a.b");
        await Assert.That(method.In.Sequential[1].PadTo).IsEqualTo(4);
        await Assert.That(method.Out.Tlv!.Count).IsEqualTo(2);
        await Assert.That(method.Out.Sequential).IsNull();
    }

    [Test]
    [Arguments("\"in\": 5")]
    [Arguments("\"in\": [ { \"type_reference\": \"uint8\", \"colour\": 1 } ]")]
    [Arguments("\"in\": { \"1\": \"uint8\", \"1\": \"uint16\" }")]
    [Arguments("\"in\": [ null ]")]
    public async Task AMalformedParameterListIsOneWarning(string inList)
    {
        string json = "{ \"schema_version\": \"1.0\", \"services\": [ { \"service_id\": 1, \"major_version\": 1, \"name\": \"S\", \"methods\": ["
            + "{ \"kind\": \"method\", \"id\": 1, \"name\": \"A\", " + inList + ", \"out\": [] } ] } ] }";
        List<SettingsLoadWarning> warnings = [];

        SomeIpConfig? config = _LoadText(json, warnings);

        await Assert.That(config).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SerializingAndLoadingAgainGivesTheSameDocument()
    {
        List<SettingsLoadWarning> warnings = [];
        foreach (string name in new[] { "full_service.json", "unions_bitfields_strings.json" })
        {
            using FileStream stream = ShippedConfigFixture.ReadExample("someip", name);
            SomeIpConfig first = _Load(stream, warnings)!;

            string written = JsonSerializer.Serialize(first, SomeIpConfigJsonContext.Default.SomeIpConfig);
            SomeIpConfig second = _LoadText(written, warnings)!;
            string writtenAgain = JsonSerializer.Serialize(second, SomeIpConfigJsonContext.Default.SomeIpConfig);

            await Assert.That(writtenAgain).IsEqualTo(written);
        }

        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    #endregion

    #region Schema version

    [Test]
    [Arguments("1.0")]
    [Arguments("1.7")]
    [Arguments("1.12")]
    public async Task SupportedMajorVersionLoadsAndMinorDifferencesAreAccepted(string version)
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig config = _LoadText(_ConfigWithServices(version, 1), warnings)!;

        bool accepted = SomeIpConfigLoader.TryAccept(config, 10, 10, warnings);

        await Assert.That(accepted).IsTrue();
        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ANewerMajorVersionIsOneWarningThatNamesFoundAndSupported()
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig config = _LoadText(_ConfigWithServices("2.0", 1), warnings)!;

        bool accepted = SomeIpConfigLoader.TryAccept(config, 10, 10, warnings);

        await Assert.That(accepted).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("major 2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("major version is 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.DeserializationError);
    }

    [Test]
    public async Task AMissingVersionIsRefusedAsMissing()
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig config = _LoadText("{ \"services\": [] }", warnings)!;

        bool accepted = SomeIpConfigLoader.TryAccept(config, 10, 10, warnings);

        await Assert.That(accepted).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("missing", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("x.1")]
    [Arguments("1")]
    [Arguments("1.")]
    [Arguments(".1")]
    [Arguments("1.2.3")]
    [Arguments("-1.0")]
    [Arguments("1.0 ")]
    public async Task AMalformedVersionIsRefusedAsMalformed(string version)
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig config = _LoadText(_ConfigWithServices(version, 1), warnings)!;

        bool accepted = SomeIpConfigLoader.TryAccept(config, 10, 10, warnings);

        await Assert.That(accepted).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("malformed", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AMajorVersionBeyondTheIntRangeIsMalformedNotAnException()
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig config = _LoadText(_ConfigWithServices("99999999999.0", 1), warnings)!;

        bool accepted = SomeIpConfigLoader.TryAccept(config, 10, 10, warnings);

        await Assert.That(accepted).IsFalse();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    #endregion

    #region Size limits

    [Test]
    public async Task TheServiceCountLimitRefusesOneAboveAndAcceptsTheLimit()
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig four = _LoadText(_ConfigWithServices("1.0", 4), warnings)!;
        SomeIpConfig three = _LoadText(_ConfigWithServices("1.0", 3), warnings)!;

        bool refused = SomeIpConfigLoader.TryAccept(four, 3, 100, warnings);
        int afterRefusal = warnings.Count;
        bool accepted = SomeIpConfigLoader.TryAccept(three, 3, 100, warnings);

        await Assert.That(refused).IsFalse();
        await Assert.That(afterRefusal).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("someip.config_max_services", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("Limit: 3", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Kind).IsEqualTo(SettingsLoadWarningKind.OutOfRange);
        await Assert.That(accepted).IsTrue();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TheTypeCountLimitIsCountedOverAllServices()
    {
        List<SettingsLoadWarning> warnings = [];
        SomeIpConfig six = _LoadText(_ConfigWithServices("1.0", 2, typesPerService: 3), warnings)!;
        SomeIpConfig five = _LoadText(_ConfigWithServices("1.0", 1, typesPerService: 5), warnings)!;

        bool refused = SomeIpConfigLoader.TryAccept(six, 100, 5, warnings);
        bool accepted = SomeIpConfigLoader.TryAccept(five, 100, 5, warnings);

        await Assert.That(refused).IsFalse();
        await Assert.That(accepted).IsTrue();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("someip.config_max_types", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("Limit: 5", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task TheSizeLimitsDefaultToTheValuesOfTheLimitDefinitions()
    {
        await Assert.That(SomeIpLimits.ConfigMaxServices.Default).IsEqualTo(10_000UL);
        await Assert.That(SomeIpLimits.ConfigMaxTypes.Default).IsEqualTo(1_000_000UL);
        await Assert.That(SomeIpLimits.ConfigMaxServices.Key).IsEqualTo("someip.config_max_services");
        await Assert.That(SomeIpLimits.ConfigMaxTypes.Key).IsEqualTo("someip.config_max_types");
    }

    [Test]
    public async Task EveryLimitIsCompleteAndInRange()
    {
        await Assert.That(SomeIpLimits.All.Count).IsEqualTo(10);
        foreach (SomeIpLimitDefinition limit in SomeIpLimits.All)
        {
            await Assert.That(limit.Key.StartsWith("someip.", StringComparison.Ordinal)).IsTrue();
            await Assert.That(limit.Minimum <= limit.Default && limit.Default <= limit.Maximum).IsTrue();
            await Assert.That(limit.Texts.Title.Length).IsGreaterThan(0);
            await Assert.That(limit.Texts.Title.EndsWith('.')).IsFalse();
            string[] texts =
            [
                limit.Texts.Description,
                limit.Texts.Reason,
                limit.Texts.Benefit,
                limit.Texts.SmallerValues,
                limit.Texts.LargerValues,
                limit.Texts.OffEffect,
            ];
            foreach (string text in texts)
            {
                await Assert.That(text.Length).IsGreaterThan(0);
            }
        }
    }

    #endregion
}
