// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests;

/// <summary>
/// Tests for the SOME/IP configuration compiler: services and versions, member kinds, the type graph, the name rule, name
/// collisions, duplicates, cycles, partial loading, invalid values, and that the shipped examples compile cleanly.
/// </summary>
internal sealed class SomeIpConfigCompilerTests
{
    private static (SomeIpCompiledModel Model, List<SettingsLoadWarning> Warnings) _Compile(string json, string prefix = "")
    {
        List<SettingsLoadWarning> warnings = [];
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        if (!SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings)
            || !SomeIpConfigLoader.TryAccept(config, 10_000, 1_000_000, warnings))
        {
            return (SomeIpCompiledModel.Empty, warnings);
        }

        return (SomeIpConfigCompiler.Compile(config, prefix, warnings), warnings);
    }

    /// <summary>One service with the given members and types (JSON fragments).</summary>
    private static string _Service(string members, string types = "", int id = 4097, int major = 1, string name = "Svc") =>
        $$"""{ "service_id": {{id}}, "major_version": {{major}}, "name": "{{name}}", "methods": [ {{members}} ], "data_types": [ {{types}} ] }""";

    private static string _File(params string[] services) =>
        $$"""{ "schema_version": "1.0", "services": [ {{string.Join(", ", services)}} ] }""";

    private static string _Method(int id, string name, string inList = "[]", string outList = "[]", string extra = "") =>
        $$"""{ "kind": "method", "id": {{id}}, "name": "{{name}}", "in": {{inList}}, "out": {{outList}}{{extra}} }""";

    private static List<string> _Names(SomeIpCompiledModel model) =>
        [.. model.Sites.ToArray().Select(static site => site.RegisteredName)];

    private static bool _Mentions(List<SettingsLoadWarning> warnings, string first, string second) =>
        warnings.Any(w => w.Message.Contains(first, StringComparison.Ordinal) && w.Message.Contains(second, StringComparison.Ordinal));

    #region Services and members

    [Test]
    public async Task TwoMajorVersionsOfOneServiceCompileAndResolveByInterfaceVersion()
    {
        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(_Method(1, "Old"), name: "Svc", major: 1),
            _Service(_Method(1, "New"), name: "Svc", major: 2)));

        bool v2 = model.TryGetService(4097, 2, out int index2);
        bool v1 = model.TryGetService(4097, 1, out int index1);
        bool v3 = model.TryGetService(4097, 3, out _);
        bool anyName = model.TryGetServiceName(4097, out string? name);

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(model.Services.Length).IsEqualTo(2);
        await Assert.That(v2 && v1).IsTrue();
        await Assert.That(model.Services.Span[index2].Members.Span[model.Services.Span[index2].MemberById[1]].Name).IsEqualTo("New");
        await Assert.That(model.Services.Span[index1].Members.Span[model.Services.Span[index1].MemberById[1]].Name).IsEqualTo("Old");
        await Assert.That(v3).IsFalse();
        await Assert.That(anyName).IsTrue();
        await Assert.That(name).IsEqualTo("Svc");
    }

    [Test]
    public async Task AllFourMemberKindsResolve()
    {
        const string Members = """
            { "kind": "method", "id": 1, "name": "Call", "in": [], "out": [] },
            { "kind": "event", "id": 32769, "name": "Evt", "type_reference": "uint8" },
            { "kind": "field", "name": "Fld", "get_id": 2, "set_id": 3, "notifier_id": 32770, "type_reference": "uint8" },
            { "kind": "event_group", "id": 7, "name": "Events" }
            """;

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(Members)));

        SomeIpServiceInfo service = model.Services.Span[0];
        SomeIpMemberKind Kind(ushort id) => service.Members.Span[service.MemberById[id]].Kind;
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(Kind(1)).IsEqualTo(SomeIpMemberKind.Method);
        await Assert.That(Kind(32769)).IsEqualTo(SomeIpMemberKind.Event);
        await Assert.That(Kind(2)).IsEqualTo(SomeIpMemberKind.FieldGetter);
        await Assert.That(Kind(3)).IsEqualTo(SomeIpMemberKind.FieldSetter);
        await Assert.That(Kind(32770)).IsEqualTo(SomeIpMemberKind.FieldNotifier);
        await Assert.That(service.EventGroupNames[7]).IsEqualTo("Events");
        await Assert.That(service.MemberById.ContainsKey(7)).IsFalse();
    }

    #endregion

    #region Type graph

    [Test]
    public async Task EveryCategoryCompilesAndEveryReferenceIsAnIndex()
    {
        const string Types = """
            { "category": "base_type", "name": "u16", "type_reference": "uint16" },
            { "category": "array", "name": "u16s", "type_reference": "u16" },
            { "category": "structure", "name": "inner", "members": [ { "name": "values", "type_reference": "u16s" } ] },
            { "category": "union", "name": "choice", "members": { "1": { "name": "a", "type_reference": "u16" }, "2": { "name": "b", "type_reference": "uint8" } } },
            { "category": "bitfield", "name": "flags", "bits": 8, "members": { "0": { "name": "ready" }, "7": { "name": "fault" } } },
            { "category": "alias", "name": "speed", "type_reference": "u16" },
            { "category": "tlv_structure", "name": "message", "members": {
                "1": { "name": "inner", "type_reference": "inner" },
                "2": { "name": "choice", "type_reference": "choice" },
                "3": { "name": "flags", "type_reference": "flags" },
                "4": { "name": "speed", "type_reference": "speed" } } }
            """;

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(_Method(1, "Get", "[]", "[\"message\"]"), Types)));

        SomeIpMemberInfo member = model.Services.Span[0].Members.Span[0];
        List<SomeIpTypeCategory> categories = [.. model.Types.ToArray().Select(static t => t.Category)];
        SomeIpUsageSite[] sites = model.Sites.ToArray();
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(member.Out.SitesCount).IsEqualTo(1);
        await Assert.That(categories.Contains(SomeIpTypeCategory.TlvStructure)).IsTrue();
        await Assert.That(categories.Contains(SomeIpTypeCategory.Union)).IsTrue();
        await Assert.That(categories.Contains(SomeIpTypeCategory.Bitfield)).IsTrue();
        await Assert.That(categories.Contains(SomeIpTypeCategory.Array)).IsTrue();
        await Assert.That(categories.Contains(SomeIpTypeCategory.Alias)).IsFalse();
        await Assert.That(sites.All(static site => site.TypeIndex >= 0)).IsTrue();

        // The alias is resolved: its site decodes as the base type and keeps the alias name.
        SomeIpUsageSite speed = sites.Single(static site => site.RegisteredName.EndsWith(".speed", StringComparison.Ordinal));
        await Assert.That(model.Types.Span[speed.TypeIndex].Category).IsEqualTo(SomeIpTypeCategory.Base);
        await Assert.That(model.Types.Span[speed.TypeIndex].Primitive).IsEqualTo(SomeIpPrimitive.UInt16);

        // The TLV list resolves members by id; the union by selector; the bitfield by bit.
        SomeIpUsageSite message = sites[member.Out.SitesStart];
        await Assert.That(message.ChildByKey!.Count).IsEqualTo(4);
        SomeIpUsageSite choice = sites[message.ChildByKey[2]];
        await Assert.That(choice.ChildByKey!.Count).IsEqualTo(2);
        SomeIpUsageSite flags = sites[message.ChildByKey[3]];
        await Assert.That(flags.ChildByKey![7]).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task OneTypeUsedInThreePlacesIsOneTypeInfoAndThreeSiteSets()
    {
        const string Types = """
            { "category": "structure", "name": "point", "members": [ { "name": "x", "type_reference": "uint8" } ] },
            { "category": "structure", "name": "line", "members": [ { "name": "a", "type_reference": "point" } ] }
            """;
        string members = string.Join(
            ", ",
            _Method(1, "A", "[ { \"name\": \"p\", \"type_reference\": \"point\" } ]"),
            _Method(2, "B", "[ { \"name\": \"p\", \"type_reference\": \"point\" } ]"),
            _Method(3, "C", "[ { \"name\": \"l\", \"type_reference\": \"line\" } ]"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, Types)));

        List<string> xNames = [.. _Names(model).Where(static n => n.EndsWith(".x", StringComparison.Ordinal))];
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(model.Types.ToArray().Count(static t => t.Name == "point")).IsEqualTo(1);
        await Assert.That(xNames.Count).IsEqualTo(3);
        await Assert.That(xNames.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(3);
        await Assert.That(xNames.Contains("Svc.v1.A.req.p.x")).IsTrue();
        await Assert.That(xNames.Contains("Svc.v1.B.req.p.x")).IsTrue();
        await Assert.That(xNames.Contains("Svc.v1.C.req.l.a.x")).IsTrue();
    }

    [Test]
    public async Task EachUsageSiteGetsItsOwnSubtreeOfSites()
    {
        const string Types = """{ "category": "structure", "name": "point", "members": [ { "name": "x", "type_reference": "uint8" } ] }""";
        string members = string.Join(
            ", ",
            _Method(1, "A", "[ { \"name\": \"p\", \"type_reference\": \"point\" } ]"),
            _Method(2, "B", "[ { \"name\": \"p\", \"type_reference\": \"point\" } ]"));

        (SomeIpCompiledModel model, _) = _Compile(_File(_Service(members, Types)));

        SomeIpServiceInfo service = model.Services.Span[0];
        SomeIpUsageSite[] sites = model.Sites.ToArray();
        SomeIpUsageSite pa = sites[service.Members.Span[0].In.SitesStart];
        SomeIpUsageSite pb = sites[service.Members.Span[1].In.SitesStart];
        await Assert.That(pa.TypeIndex).IsEqualTo(pb.TypeIndex);
        await Assert.That(pa.ChildrenStart).IsNotEqualTo(pb.ChildrenStart);
        await Assert.That(sites[pa.ChildrenStart].RegisteredName).IsNotEqualTo(sites[pb.ChildrenStart].RegisteredName);
    }

    #endregion

    #region Names
    private const string _TemperatureTypes = """
        { "category": "structure", "name": "temperature_request", "members": [
            { "name": "target_c", "type_reference": "int16" }, { "name": "zone", "type_reference": "uint8" } ] }
        """;

    private static SomeIpUsageSite _SiteNamed(SomeIpCompiledModel model, string name) =>
        model.Sites.ToArray().Single(site => site.RegisteredName == name);

    [Test]
    public async Task NamesAreUsedUnchangedAsSegmentsWithTheirExactCase()
    {
        string members = string.Join(
            ", ",
            _Method(1, "SetTemperature", "[ { \"name\": \"request\", \"type_reference\": \"temperature_request\" } ]"),
            _Method(2, "_private1"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, _TemperatureTypes, name: "ClimateControl", major: 2)));

        List<string> names = _Names(model);
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(names.Contains("ClimateControl.v2.SetTemperature.req.request")).IsTrue();
        await Assert.That(names.Contains("ClimateControl.v2.SetTemperature.req.request.target_c")).IsTrue();
        await Assert.That(names.Contains("ClimateControl.v2.SetTemperature.req.request.zone")).IsTrue();
        await Assert.That(names.Any(static n => n.Contains("climatecontrol", StringComparison.Ordinal))).IsFalse();
        await Assert.That(model.Services.Span[0].Members.Span[1].Segment).IsEqualTo("_private1");
        await Assert.That(model.Services.Span[0].Path).IsEqualTo("ClimateControl.v2");
        await Assert.That(model.Services.Span[0].Members.Span[0].Path).IsEqualTo("ClimateControl.v2.SetTemperature");
    }

    [Test]
    public async Task ThePathFollowsTheMemberKindWithDirectionSegmentsAndDefaults()
    {
        const string Types = """
            { "category": "base_type", "name": "temperature", "type_reference": "int16" },
            { "category": "base_type", "name": "fan_speed", "type_reference": "uint8" },
            { "category": "structure", "name": "cell", "members": [ { "name": "x", "type_reference": "uint8" } ] },
            { "category": "array", "name": "rows_t", "type_reference": "cell" },
            { "category": "bitfield", "name": "flags_t", "bits": 8, "members": { "0": { "name": "ready" } } }
            """;
        string members = string.Join(
            ", ",
            _Method(1, "M", "[ { \"name\": \"a\", \"type_reference\": \"uint8\" } ]", "[ { \"name\": \"b\", \"type_reference\": \"uint8\" } ]"),
            """{ "kind": "event", "id": 32769, "name": "E", "type_reference": "temperature" }""",
            """{ "kind": "field", "name": "F", "get_id": 2, "set_id": 3, "notifier_id": 32770, "type_reference": "fan_speed" }""",
            _Method(4, "T", "[]", "{ \"7\": \"uint8\" }"),
            _Method(5, "R", "[ { \"name\": \"rows\", \"type_reference\": \"rows_t\" } ]"),
            _Method(6, "B", "[ { \"name\": \"flags\", \"type_reference\": \"flags_t\" } ]"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, Types, name: "S")));

        List<string> names = _Names(model);
        await Assert.That(warnings.Count).IsEqualTo(0);
        foreach (string expected in new[]
        {
            "S.v1.M.req.a", "S.v1.M.resp.b", "S.v1.E.temperature", "S.v1.F_get.resp.fan_speed", "S.v1.F_set.req.fan_speed",
            "S.v1.F_set.resp.fan_speed", "S.v1.F_notifier.fan_speed", "S.v1.T.resp.member_7", "S.v1.R.req.rows.item.x",
            "S.v1.B.req.flags.ready",
        })
        {
            await Assert.That(names.Contains(expected)).IsTrue().Because(expected);
        }

        // The getter request has no site: a getter asks for the value and carries none.
        SomeIpMemberInfo getter = model.Services.Span[0].Members.ToArray().Single(static m => m.Kind == SomeIpMemberKind.FieldGetter);
        await Assert.That(getter.In.SitesCount).IsEqualTo(0);
        await Assert.That(getter.Segment).IsEqualTo("F_get");
    }

    [Test]
    public async Task AServiceWithAnInvalidNameIsSkippedAndTheOthersCompile()
    {
        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(_Method(1, "Get"), id: 0x1001, name: "Climate-Control"),
            _Service(_Method(1, "Get"), id: 0x1002, name: "Body")));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("Climate-Control").And.Contains("services[0]").And.Contains("not a valid name");
        await Assert.That(model.TryGetService(0x1001, 1, out _)).IsFalse();
        await Assert.That(model.TryGetService(0x1002, 1, out _)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("2Door")]
    [Arguments("Door.Front")]
    public async Task AServiceNameThatBreaksTheSegmentRuleIsRefused(string name)
    {
        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(_Method(1, "Get"), name: name)));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(model.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task AMemberWithAnInvalidNameIsNotRegisteredAndTheOthersAre()
    {
        string members = string.Join(", ", _Method(1, "Set Temp"), _Method(2, "Get"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members)));

        SomeIpServiceInfo service = model.Services.Span[0];
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("Set Temp");
        await Assert.That(service.MemberById.ContainsKey(1)).IsFalse();
        await Assert.That(service.MemberById.ContainsKey(2)).IsTrue();
    }

    [Test]
    public async Task ADotInANameIsRefusedBecauseItWouldAddALevelTheDescriptionDoesNotHave()
    {
        string members = string.Join(
            ", ",
            _Method(1, "Set.Temperature"),
            _Method(2, "Fine", "[ { \"name\": \"a.b\", \"type_reference\": \"uint8\" } ]"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members)));

        SomeIpServiceInfo service = model.Services.Span[0];
        await Assert.That(warnings.Count).IsEqualTo(2);
        await Assert.That(service.MemberById.ContainsKey(1)).IsFalse();
        await Assert.That(model.Sites.Span[service.Members.Span[service.MemberById[2]].In.SitesStart].Defect).Contains("a.b");
    }

    [Test]
    public async Task ASiteWithAnInvalidNameIsADefectWithoutAFieldName()
    {
        const string Types = """{ "category": "structure", "name": "rec", "members": [ { "name": "a", "type_reference": "uint8" }, { "name": "zone-id", "type_reference": "uint8" } ] }""";

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(_Method(1, "Get", "[ { \"name\": \"p\", \"type_reference\": \"rec\" } ]"), Types)));

        SomeIpUsageSite parameter = model.Sites.Span[model.Services.Span[0].Members.Span[0].In.SitesStart];
        SomeIpUsageSite broken = model.Sites.Span[parameter.ChildrenStart + 1];
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(broken.Defect).Contains("zone-id");
        await Assert.That(broken.RegisteredName).IsEqualTo("");
        await Assert.That(model.Sites.Span[parameter.ChildrenStart].Defect).IsEqualTo("");
    }

    [Test]
    public async Task TwoServicesWithTheSameNameAndVersionKeepTheFirst()
    {
        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(_Method(1, "Get"), id: 0xF232, name: "ClimateControl", major: 2),
            _Service(_Method(1, "Get"), id: 0xF233, name: "ClimateControl", major: 2),
            _Service(_Method(1, "Get"), id: 0xF234, name: "ClimateControl", major: 1)));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(_Mentions(warnings, "services[1]", "services[0]")).IsTrue();
        await Assert.That(model.TryGetService(0xF232, 2, out _)).IsTrue();
        await Assert.That(model.TryGetService(0xF233, 2, out _)).IsFalse();
        await Assert.That(model.TryGetService(0xF234, 1, out _)).IsTrue();
    }

    [Test]
    public async Task TwoMembersWithTheSameNameKeepTheFirstAndAFieldAccessorCountsAsAMember()
    {
        (SomeIpCompiledModel methods, List<SettingsLoadWarning> methodWarnings) = _Compile(_File(_Service(
            string.Join(", ", _Method(1, "SetTemperature"), _Method(2, "SetTemperature")))));

        string withField = string.Join(
            ", ",
            """{ "kind": "field", "name": "Fan", "get_id": 3, "type_reference": "uint8" }""",
            _Method(4, "Fan_get"));
        (SomeIpCompiledModel accessor, List<SettingsLoadWarning> accessorWarnings) = _Compile(_File(_Service(withField)));

        await Assert.That(methodWarnings.Count).IsEqualTo(1);
        await Assert.That(_Mentions(methodWarnings, "methods[1]", "methods[0]")).IsTrue();
        await Assert.That(methods.Services.Span[0].MemberById.ContainsKey(1)).IsTrue();
        await Assert.That(methods.Services.Span[0].MemberById.ContainsKey(2)).IsFalse();
        await Assert.That(accessorWarnings.Count).IsEqualTo(1);
        await Assert.That(_Mentions(accessorWarnings, "Fan_get", "get_id")).IsTrue();
        await Assert.That(accessor.Services.Span[0].MemberById.ContainsKey(3)).IsTrue();
        await Assert.That(accessor.Services.Span[0].MemberById.ContainsKey(4)).IsFalse();
    }

    [Test]
    public async Task SiblingsWithTheSameNameKeepTheFirstIncludingTheTlvDefault()
    {
        const string Types = """
            { "category": "structure", "name": "twice", "members": [ { "name": "x", "type_reference": "uint8" }, { "name": "x", "type_reference": "uint8" } ] },
            { "category": "tlv_structure", "name": "tagged", "members": { "3": { "name": "member_7", "type_reference": "uint8" }, "7": { "type_reference": "uint8" } } },
            { "category": "base_type", "name": "zone_name", "type_reference": "uint8" },
            { "category": "bitfield", "name": "flags_t", "bits": 8, "members": { "0": { "name": "ready" }, "1": { "name": "ready" } } }
            """;
        string members = string.Join(
            ", ",
            _Method(1, "A", "[ { \"name\": \"p\", \"type_reference\": \"twice\" } ]"),
            _Method(2, "B", "[ { \"name\": \"t\", \"type_reference\": \"tagged\" } ]"),
            _Method(3, "C", "[ \"zone_name\", \"zone_name\" ]"),
            _Method(4, "D", "[ { \"name\": \"f\", \"type_reference\": \"flags_t\" } ]"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, Types)));

        SomeIpServiceInfo service = model.Services.Span[0];
        SomeIpUsageSite[] sites = model.Sites.ToArray();
        SomeIpUsageSite twice = sites[sites[service.Members.Span[0].In.SitesStart].ChildrenStart + 1];
        SomeIpUsageSite tagged = sites[sites[service.Members.Span[1].In.SitesStart].ChildrenStart];
        SomeIpUsageSite taggedLater = sites[sites[service.Members.Span[1].In.SitesStart].ChildrenStart + 1];
        SomeIpUsageSite secondZone = sites[service.Members.Span[2].In.SitesStart + 1];
        await Assert.That(warnings.Count).IsEqualTo(4);
        await Assert.That(twice.Defect).Contains("members[0]");
        await Assert.That(tagged.Defect).IsEqualTo("");
        await Assert.That(taggedLater.Defect).Contains("member_7");
        await Assert.That(secondZone.Defect).Contains("zone_name");
        await Assert.That(sites[service.Members.Span[3].In.SitesStart].ChildrenCount).IsEqualTo(1);
    }

    [Test]
    public async Task NoNameIsEverInventedWithASuffix()
    {
        string members = string.Join(
            ", ",
            _Method(1, "A", "[ { \"name\": \"p\", \"type_reference\": \"rec\" }, { \"name\": \"p\", \"type_reference\": \"rec\" } ]"),
            _Method(2, "A"),
            _Method(3, "B", "[ \"rec\", \"rec\" ]"));
        const string Types = """{ "category": "structure", "name": "rec", "members": [ { "name": "x", "type_reference": "uint8" }, { "name": "x", "type_reference": "uint8" } ] }""";

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(members, Types, id: 1, name: "S"),
            _Service(members, Types, id: 2, name: "S")));

        await Assert.That(warnings.Count).IsGreaterThan(0);
        await Assert.That(_Names(model).Any(static n => System.Text.RegularExpressions.Regex.IsMatch(n, "_[0-9]+$"))).IsFalse();
        List<string> registered = [.. _Names(model).Where(static n => n.Length > 0)];
        await Assert.That(registered.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(registered.Count);
    }

    [Test]
    public async Task ADisplayNameIsTakenFromUiNameOrTheNameAndAnInvalidOneWarns()
    {
        const string Json = """
            { "schema_version": "1.0", "services": [ { "service_id": 62002, "major_version": 2, "name": "ClimateControl", "ui_name": "Climate Control",
              "methods": [ { "kind": "method", "id": 1, "name": "SetTemperature", "in": [ { "name": "request", "ui_name": "Requested\nTemperature", "type_reference": "uint8" } ], "out": [] } ],
              "data_types": [] } ] }
            """;

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(Json);

        SomeIpServiceInfo service = model.Services.Span[0];
        SomeIpMemberInfo method = service.Members.Span[0];
        await Assert.That(service.UiName).IsEqualTo("Climate Control");
        await Assert.That(method.UiName).IsEqualTo("SetTemperature");
        await Assert.That(model.Sites.Span[method.In.SitesStart].UiName).IsEqualTo("request");
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("ui_name").And.Contains("parameter 0");
    }

    [Test]
    [Arguments("", "ClimateControl.v2.SetTemperature.req.request.zone")]
    [Arguments("acme", "acme.ClimateControl.v2.SetTemperature.req.request.zone")]
    [Arguments("acme.car", "acme.car.ClimateControl.v2.SetTemperature.req.request.zone")]
    public async Task ThePrefixIsPutInFrontOfEveryFilterNameButNeverOfAGroupName(string prefix, string expected)
    {
        string members = _Method(1, "SetTemperature", "[ { \"name\": \"request\", \"type_reference\": \"temperature_request\" } ]");

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(
            _File(_Service(members, _TemperatureTypes, name: "ClimateControl", major: 2)),
            prefix);

        SomeIpServiceInfo service = model.Services.Span[0];
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(_Names(model).Contains(expected)).IsTrue();
        await Assert.That(service.Path.EndsWith("ClimateControl.v2", StringComparison.Ordinal)).IsTrue();
        await Assert.That(service.Members.Span[0].Path).IsEqualTo(expected[..expected.IndexOf(".req.", StringComparison.Ordinal)]);
        await Assert.That(service.GroupName).IsEqualTo("someip.payload.ClimateControl.v2");
        await Assert.That(service.Members.Span[0].GroupName).IsEqualTo("someip.payload.ClimateControl.v2.SetTemperature");
    }

    [Test]
    public async Task TheSchemaOfEveryShippedExampleDeclaresNoFilterNameAndTheFullExampleShowsUiNames()
    {
        string schema = File.ReadAllText(Path.Combine(ShippedConfigFixture.SchemasRoot, "someip-config.schema.json"));
        using JsonDocument document = JsonDocument.Parse(schema);
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        string full = File.ReadAllText(ShippedConfigFixture.ExamplePath("someip", "full_service.json"));
        using JsonDocument example = JsonDocument.Parse(full);
        JsonElement firstService = example.RootElement.GetProperty("services")[0];

        await Assert.That(schema.Contains("filter_name", StringComparison.Ordinal)).IsFalse();
        await Assert.That(definitions.GetProperty("service").GetProperty("properties").TryGetProperty("ui_name", out _)).IsTrue();
        await Assert.That(definitions.GetProperty("method_call").GetProperty("properties").TryGetProperty("ui_name", out _)).IsTrue();
        await Assert.That(definitions.GetProperty("service").GetProperty("properties").GetProperty("name").GetProperty("pattern").GetString())
            .IsEqualTo("^[A-Za-z_][A-Za-z0-9_]*$");
        await Assert.That(full.Contains("filter_name", StringComparison.Ordinal)).IsFalse();
        await Assert.That(firstService.TryGetProperty("ui_name", out _)).IsTrue();
        await Assert.That(firstService.GetProperty("methods")[0].TryGetProperty("ui_name", out _)).IsTrue();
    }
    #endregion

    #region Duplicates, cycles, partial loading

    [Test]
    public async Task EveryKindOfDuplicateIsOneWarningAndTheFirstDefinitionDecodes()
    {
        const string Types = """
            { "category": "base_type", "name": "t", "type_reference": "uint8" },
            { "category": "base_type", "name": "t", "type_reference": "uint16" }
            """;
        string members = string.Join(
            ", ",
            _Method(1, "First", "[ \"t\" ]"),
            _Method(1, "Second"),
            """{ "kind": "field", "name": "Fld", "set_id": 1, "type_reference": "t" }""");

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(members, Types, major: 1),
            _Service(_Method(9, "Other"), major: 1)));

        SomeIpServiceInfo service = model.Services.Span[0];
        await Assert.That(warnings.Count).IsEqualTo(4);
        await Assert.That(_Mentions(warnings, "services[1]", "services[0]")).IsTrue();
        await Assert.That(_Mentions(warnings, "data_types[1]", "data_types[0]")).IsTrue();
        await Assert.That(warnings.Count(static w => w.Message.Contains("repeats methods[0]", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(model.Services.Length).IsEqualTo(1);
        await Assert.That(service.Members.Span[service.MemberById[1]].Name).IsEqualTo("First");
        await Assert.That(model.Types.Span[model.Sites.Span[service.Members.Span[0].In.SitesStart].TypeIndex].Primitive).IsEqualTo(SomeIpPrimitive.UInt8);
    }

    [Test]
    public async Task CyclesAreOneWarningEachAndOnlyTheirTypesAndArraysOfThemAreUnavailable()
    {
        const string Types = """
            { "category": "structure", "name": "A", "members": [ { "name": "b", "type_reference": "B" } ] },
            { "category": "structure", "name": "B", "members": [ { "name": "a", "type_reference": "A" } ] },
            { "category": "alias", "name": "X", "type_reference": "Y" },
            { "category": "alias", "name": "Y", "type_reference": "X" },
            { "category": "structure", "name": "C", "members": [ { "name": "v", "type_reference": "uint8" } ] },
            { "category": "array", "name": "As", "type_reference": "A" }
            """;
        string members = _Method(1, "Get", "[]", "[ { \"name\": \"a\", \"type_reference\": \"A\" }, { \"name\": \"c\", \"type_reference\": \"C\" }, { \"name\": \"as\", \"type_reference\": \"As\" } ]");

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, Types)));

        SomeIpMemberInfo member = model.Services.Span[0].Members.Span[0];
        SomeIpUsageSite[] sites = model.Sites.ToArray();
        SomeIpTypeCategory CategoryOf(int offset) => model.Types.Span[sites[member.Out.SitesStart + offset].TypeIndex].Category;
        await Assert.That(warnings.Count).IsEqualTo(2);
        await Assert.That(_Mentions(warnings, "'A'", "'B'")).IsTrue();
        await Assert.That(_Mentions(warnings, "'X'", "'Y'")).IsTrue();
        await Assert.That(CategoryOf(0)).IsEqualTo(SomeIpTypeCategory.Unavailable);
        await Assert.That(CategoryOf(1)).IsEqualTo(SomeIpTypeCategory.Structure);
        await Assert.That(CategoryOf(2)).IsEqualTo(SomeIpTypeCategory.Unavailable);
        await Assert.That(sites[member.Out.SitesStart].ChildrenCount).IsEqualTo(0);
    }

    [Test]
    public async Task OneDanglingReferenceAmongTenServicesIsOneWarningAndEverythingElseCompiles()
    {
        List<string> services = [];
        for (int i = 0; i < 10; i++)
        {
            services.Add(_Service(_Method(1, "Get", "[]", "[ \"uint8\" ]"), id: 5000 + i, name: $"Ok{i}"));
        }

        services.Add(_Service(_Method(1, "Broken", "[ \"uint8\", { \"name\": \"p\", \"type_reference\": \"missing\" }, \"uint16\" ]"), id: 6000, name: "Defective"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File([.. services]));

        SomeIpServiceInfo defective = model.Services.Span[10];
        SomeIpMemberInfo broken = defective.Members.Span[0];
        SomeIpUsageSite[] sites = model.Sites.ToArray();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("Defective", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("methods[0]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("parameter 1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("missing", StringComparison.Ordinal)).IsTrue();
        await Assert.That(model.Services.Length).IsEqualTo(11);
        await Assert.That(broken.In.SitesCount).IsEqualTo(3);
        await Assert.That(sites[broken.In.SitesStart].TypeIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(sites[broken.In.SitesStart + 1].TypeIndex).IsEqualTo(-1);
        await Assert.That(sites[broken.In.SitesStart + 1].ReferenceName).IsEqualTo("missing");
        await Assert.That(sites[broken.In.SitesStart + 2].TypeIndex).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task ADanglingReferenceInsideASharedTypeWarnsOnceNotPerUsage()
    {
        const string Types = """{ "category": "structure", "name": "s", "members": [ { "name": "m", "type_reference": "nowhere" } ] }""";
        string members = string.Join(", ", _Method(1, "A", "[ \"s\" ]"), _Method(2, "B", "[ \"s\" ]"), _Method(3, "C", "[ \"s\" ]"));

        (_, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members, Types)));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("nowhere", StringComparison.Ordinal)).IsTrue();
    }

    #endregion

    #region Invalid values

    [Test]
    [Arguments("service", """{ "service_id": 65535, "major_version": 1, "name": "S" }""", "service_id 65535")]
    [Arguments("service", """{ "service_id": 1, "major_version": 256, "name": "S" }""", "major_version 256")]
    [Arguments("type", """{ "category": "tlv_structure", "name": "t", "members": { "4096": { "type_reference": "uint8" } } }""", "4096")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "uint8", "pad_to": 65 }""", "pad_to 65")]
    [Arguments("type", """{ "category": "bitfield", "name": "t", "bits": 12, "members": { "0": { "name": "a" } } }""", "bits 12")]
    [Arguments("type", """{ "category": "bitfield", "name": "t", "bits": 8, "members": { "8": { "name": "a" } } }""", "bit key '8'")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "string", "byte_order": "little_endian" }""", "byte_order")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "uint8", "encoding": "ascii" }""", "encoding")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "float", "lookup_values": { "1": "x" } }""", "lookup_values")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "uint8", "lookup_values": { "-1": "x" } }""", "lookup key '-1'")]
    [Arguments("type", """{ "category": "base_type", "name": "t", "type_reference": "uint8", "size_of_length": 2 }""", "size_of_length")]
    [Arguments("type", """{ "category": "union", "name": "t", "members": { "0": { "type_reference": "uint8" } } }""", "member key '0'")]
    public async Task AnInvalidValueIsOneWarningNamingTheNodeAndTheRuleAndTheRestLoads(string kind, string node, string expected)
    {
        string service = kind == "service"
            ? node
            : _Service(_Method(1, "Get", "[]", "[ \"uint8\" ]"), node);
        string json = _File(service, _Service(_Method(1, "Fine"), id: 9000, name: "Fine"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(json);

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains(expected, StringComparison.Ordinal)).IsTrue();
        await Assert.That(model.TryGetService(9000, 1, out _)).IsTrue();
    }

    [Test]
    public async Task AFixedLengthStringAsATlvMemberIsOneWarning()
    {
        const string Types = """
            { "category": "base_type", "name": "name8", "type_reference": "string", "fixed_length": 8 },
            { "category": "tlv_structure", "name": "t", "members": { "1": { "name": "n", "type_reference": "name8" } } }
            """;

        (_, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(_Method(1, "A", "[]", "[ \"t\" ]"), Types)));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("fixed_length", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("TLV", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task AnUnknownPrimitiveMakesThatTypeUnavailableWithOneWarning()
    {
        List<SettingsLoadWarning> warnings = [];
        using FileStream stream = ShippedConfigFixture.ReadNegative("someip", "unknown_primitive.json");
        _ = SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings);

        SomeIpCompiledModel model = SomeIpConfigCompiler.Compile(config!, "", warnings);

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("uint24", StringComparison.Ordinal)).IsTrue();
        await Assert.That(warnings[0].Message.Contains("wide_value", StringComparison.Ordinal)).IsTrue();
        await Assert.That(model.Types.ToArray().Any(static t => t.Category == SomeIpTypeCategory.Unavailable)).IsTrue();
    }

    [Test]
    public async Task ETwoEProfilesAndTransformationSizesResolve()
    {
        string members = string.Join(
            ", ",
            _Method(1, "P04", extra: ", \"e2e_profile\": \"04\", \"transformation_properties\": { \"size_of_string_length\": 2, \"size_of_struct_length\": 0 }"),
            _Method(2, "P99", extra: ", \"e2e_profile\": \"99\""),
            _Method(3, "Bad", extra: ", \"transformation_properties\": { \"size_of_array_length\": 3 }"));

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(_Service(members)));

        SomeIpMemberInfo[] all = model.Services.Span[0].Members.ToArray();
        await Assert.That(warnings.Count).IsEqualTo(2);
        await Assert.That(all[0].E2e).IsEqualTo(SomeIpE2eProfile.Profile04);
        await Assert.That(all[0].Transform).IsEqualTo(new SomeIpTransformation(2, 4, 0));
        await Assert.That(all[1].E2e).IsEqualTo(SomeIpE2eProfile.Unknown);
        await Assert.That(all[1].E2eProfileText).IsEqualTo("99");
        await Assert.That(all[2].Transform).IsEqualTo(new SomeIpTransformation(4, 4, 4));
    }

    [Test]
    public async Task ClientsAndPortsAreValidatedAndDeduplicated()
    {
        const string Json = """
            { "schema_version": "1.0", "udp_ports": [30490, 30490, 0], "tcp_ports": [30501], "clients": { "1": "Cockpit", "x": "Bad", "70000": "Big" }, "services": [] }
            """;

        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(Json);

        await Assert.That(warnings.Count).IsEqualTo(4);
        await Assert.That(model.UdpPorts.ToArray().Length).IsEqualTo(1);
        await Assert.That(model.TcpPorts.Span[0]).IsEqualTo((ushort)30501);
        await Assert.That(model.TryGetClientName(1, out string? name)).IsTrue();
        await Assert.That(name).IsEqualTo("Cockpit");
        await Assert.That(model.TryGetClientName(2, out _)).IsFalse();
    }

    [Test]
    public async Task ACombinatorialExpansionRefusesTheServiceWithOneWarning()
    {
        // Each level has two members that reference the next level, so one usage expands to 2^n sites.
        StringBuilder types = new();
        const int Levels = 22;
        for (int i = 0; i < Levels; i++)
        {
            string next = i == Levels - 1
                ? "uint8"
                : $"L{i + 1}";
            types.Append(CultureInfo.InvariantCulture, $$"""{ "category": "structure", "name": "L{{i}}", "members": [ """);
            types.Append(CultureInfo.InvariantCulture, $$"""{ "name": "a", "type_reference": "{{next}}" }, """);
            types.Append(CultureInfo.InvariantCulture, $$"""{ "name": "b", "type_reference": "{{next}}" } ] },""");
        }

        string typeList = types.ToString().TrimEnd(',');
        (SomeIpCompiledModel model, List<SettingsLoadWarning> warnings) = _Compile(_File(
            _Service(_Method(1, "Big", "[]", "[ \"L0\" ]"), typeList, id: 100, name: "Huge"),
            _Service(_Method(1, "Fine"), id: 200, name: "Fine")));

        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message.Contains("1000000", StringComparison.Ordinal)).IsTrue();
        await Assert.That(model.TryGetService(100, 1, out _)).IsFalse();
        await Assert.That(model.TryGetService(200, 1, out _)).IsTrue();
    }

    #endregion

    #region Shipped examples

    [Test]
    [Arguments("minimal_service.json")]
    [Arguments("full_service.json")]
    [Arguments("arrays_and_e2e08.json")]
    [Arguments("unions_bitfields_strings.json")]
    public async Task EachShippedExampleCompilesWithoutAWarningAndEveryReferenceResolves(string fileName)
    {
        List<SettingsLoadWarning> warnings = [];
        using FileStream stream = ShippedConfigFixture.ReadExample("someip", fileName);
        _ = SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings);

        SomeIpCompiledModel model = SomeIpConfigCompiler.Compile(config!, "", warnings);

        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(model.Services.Length).IsGreaterThanOrEqualTo(1);
        await Assert.That(model.Sites.ToArray().All(static site => site.TypeIndex >= 0)).IsTrue();
    }

    [Test]
    public async Task TheFullExampleHasTwoServicesSixMembersAndTheDocumentedNames()
    {
        List<SettingsLoadWarning> warnings = [];
        using FileStream stream = ShippedConfigFixture.ReadExample("someip", "full_service.json");
        _ = SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings);

        SomeIpCompiledModel model = SomeIpConfigCompiler.Compile(config!, "", warnings);

        List<string> names = _Names(model);
        await Assert.That(model.Services.Length).IsEqualTo(2);
        await Assert.That(model.Services.ToArray().Sum(static s => s.Members.Length)).IsEqualTo(6);
        await Assert.That(names.Contains("ClimateControl.v2.SetTemperature.req.request.target_c")).IsTrue();
        await Assert.That(names.Any(static n => n.StartsWith("ClimateControl.v2.SetTemperature.req.", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task TheSchemaVersionOfEveryExampleHasTheMajorTheLoaderSupports()
    {
        foreach (string path in ShippedConfigFixture.AllExamplePaths().Where(static p => p.Contains($"{Path.DirectorySeparatorChar}someip{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            string version = document.RootElement.GetProperty("schema_version").GetString()!;
            await Assert.That(version.Split('.')[0]).IsEqualTo(SomeIpConfigLoader.SupportedMajorVersion.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Mutations applied to one slot (a property or an array item) of an example.</summary>
    private static readonly string[] _Mutations = ["remove", "null", "number", "text", "object", "array"];

    [Test]
    public async Task NoMutationOfAnyValueOfAnExampleMakesTheLoaderOrCompilerThrow()
    {
        int variants = 0;
        List<string> failures = [];
        foreach (string path in ShippedConfigFixture.AllExamplePaths().Where(static p => p.Contains($"{Path.DirectorySeparatorChar}someip{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(path);
            int slots = _CountSlots(System.Text.Json.Nodes.JsonNode.Parse(text)!);
            for (int slot = 0; slot < slots; slot++)
            {
                foreach (string mutation in _Mutations)
                {
                    System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(text)!;
                    _ = _Mutate(root, slot, mutation);
                    try
                    {
                        List<SettingsLoadWarning> warnings = [];
                        using MemoryStream stream = new(Encoding.UTF8.GetBytes(root.ToJsonString()));
                        if (SomeIpConfigLoader.TryLoadFromStream(stream, out SomeIpConfig? config, warnings)
                            && SomeIpConfigLoader.TryAccept(config, 10_000, 1_000_000, warnings))
                        {
                            _ = SomeIpConfigCompiler.Compile(config, "", warnings);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(path)} slot {slot} {mutation}: {ex.GetType().Name} at {ex.StackTrace?.Split((char)10)[0].Trim()}");
                    }

                    variants++;
                }
            }
        }

        await Assert.That(variants).IsGreaterThan(600);
        await Assert.That(failures.Count).IsEqualTo(0).Because(string.Join(" | ", failures.Take(5)));
    }

    private static int _CountSlots(System.Text.Json.Nodes.JsonNode node)
    {
        int count = 0;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> property in obj)
            {
                count++;
                if (property.Value is not null)
                {
                    count += _CountSlots(property.Value);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (System.Text.Json.Nodes.JsonNode? item in array)
            {
                count++;
                if (item is not null)
                {
                    count += _CountSlots(item);
                }
            }
        }

        return count;
    }

    /// <summary>Mutates the slot with the given pre-order index (the order <see cref="_CountSlots"/> counts in).</summary>
    private static bool _Mutate(System.Text.Json.Nodes.JsonNode node, int target, string mutation)
    {
        int index = 0;
        return _Visit(node, target, mutation, ref index);
    }

    private static System.Text.Json.Nodes.JsonNode? _Replacement(string mutation) =>
        mutation switch
        {
            "number" => System.Text.Json.Nodes.JsonValue.Create(5),
            "text" => System.Text.Json.Nodes.JsonValue.Create("x"),
            "object" => new System.Text.Json.Nodes.JsonObject(),
            "array" => new System.Text.Json.Nodes.JsonArray(),
            _ => null,
        };

    private static bool _Visit(System.Text.Json.Nodes.JsonNode node, int target, string mutation, ref int index)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (string key in obj.Select(static p => p.Key).ToList())
            {
                if (index == target)
                {
                    if (mutation == "remove")
                    {
                        _ = obj.Remove(key);
                    }
                    else
                    {
                        obj[key] = _Replacement(mutation);
                    }

                    return true;
                }

                index++;
                if (obj[key] is { } child && _Visit(child, target, mutation, ref index))
                {
                    return true;
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (index == target)
                {
                    if (mutation == "remove")
                    {
                        array.RemoveAt(i);
                    }
                    else
                    {
                        array[i] = _Replacement(mutation);
                    }

                    return true;
                }

                index++;
                if (array[i] is { } child && _Visit(child, target, mutation, ref index))
                {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion
}
