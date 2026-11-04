// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Protocols.Tests.Infrastructure.SomeIp;

/// <summary>
/// The shared building blocks of the payload tests: a one-service description in JSON, the wire frames of its messages, the
/// registered names of its fields, and readers for the typed values of a materialized packet. A test states the description and
/// the bytes and reads the values back by the names a filter would use.
/// <para><b>Thread-safety:</b> stateless. A base class and not a static class, so a test class reads the helpers unqualified.</para>
/// </summary>
internal abstract class SomeIpPayloadKit
{
    /// <summary>The service id of the test service (16385).</summary>
    protected const ushort ServiceId = 0x4001;

    /// <summary>The filter-name prefix of the test service (service "Svc", major version 1).</summary>
    protected const string Prefix = "Svc.v1.";

    /// <summary>The index-group-name prefix of the test service; groups always start with <c>someip.payload.</c>.</summary>
    protected const string GroupPrefix = "someip.payload.Svc.v1.";

    #region Description helpers

    protected static string Json(string methods, string types = "") =>
        $$"""
        {"schema_version":"1.0","services":[{"service_id":16385,"major_version":1,"name":"Svc","methods":[{{methods}}],"data_types":[{{types}}]}]}
        """;

    protected static string Method(int id, string name, string inList, string outList, string extra = "") =>
        $$"""{"kind":"method","id":{{id}},"name":"{{name}}","in":{{inList}},"out":{{outList}}{{extra}}}""";

    protected static string Site(string name, string type, string extra = "") =>
        $$"""{"name":"{{name}}","type_reference":"{{type}}"{{extra}}}""";

    protected static string Sites(params string[] sites) => "[" + string.Join(",", sites) + "]";

    protected static string Transform(int stringLength = 4, int arrayLength = 4, int structLength = 4) =>
        $$""","transformation_properties":{"size_of_string_length":{{stringLength}},"size_of_array_length":{{arrayLength}},"size_of_struct_length":{{structLength}}}""";

    protected static string BaseType(string name, string primitive, string extra = "") =>
        $$"""{"category":"base_type","name":"{{name}}","type_reference":"{{primitive}}"{{extra}}}""";

    protected static string ArrayType(string name, string element, string extra = "") =>
        $$"""{"category":"array","name":"{{name}}","type_reference":"{{element}}"{{extra}}}""";

    protected static string StructType(string name, params string[] members) =>
        $$"""{"category":"structure","name":"{{name}}","members":[{{string.Join(",", members)}}]}""";

    protected static string Join(params string[] parts) => string.Join(",", parts);

    protected static byte[] UdpFrame(byte[] payload, ushort methodId = 1, byte messageType = 0x00) =>
        FrameBuilderTestExtensions.WrapRawUdp(SomeIpBytes.Message(ServiceId, methodId, payload, messageType: messageType), 40000, 30490);

    protected static string InName(string method, string parameter) => string.Concat(Prefix, method, ".req.", parameter);

    protected static string OutName(string method, string parameter) => string.Concat(Prefix, method, ".resp.", parameter);

    #endregion

    #region Reading helpers

    protected static List<Field> FieldsOf(SomeIpTestStack s, Packet packet, string name)
    {
        List<Field> found = [];
        FieldId? id = s.Stack.GetFieldId(name);
        if (id is null)
        {
            return found;
        }

        foreach (Field field in packet.IterFieldsDfs(materialize: true))
        {
            if (field.FieldId == id.Value)
            {
                found.Add(field);
            }
        }

        return found;
    }

    protected static List<long> I64s(SomeIpTestStack s, Packet packet, string name) =>
        [.. FieldsOf(s, packet, name).Select(static f => _I64Or(f, long.MinValue))];

    protected static List<double> F64s(SomeIpTestStack s, Packet packet, string name) =>
        [.. FieldsOf(s, packet, name).Select(static f => _F64Or(f, double.NaN))];

    protected static List<bool> Bools(SomeIpTestStack s, Packet packet, string name) =>
        [.. FieldsOf(s, packet, name).Select(static f => f.Value.Data.TryGetAsBool(out bool v) && v)];

    protected static List<string> Texts(SomeIpTestStack s, Packet packet, string name) =>
        [.. FieldsOf(s, packet, name).Select(static f => _TextOr(f, "<none>"))];

    protected static List<string> Errors(SomeIpTestStack s, Packet packet) =>
        [.. FieldsOf(s, packet, "someip.payload.error").Select(static f => _StringOr(f, ""))];

    // The value of a field of the expected kind, or the marker when it holds another kind; one arm per line.
    private static long _I64Or(Field field, long marker)
    {
        if (field.Value.Data.TryGetAsI64(out long value))
        {
            return value;
        }

        return marker;
    }

    private static double _F64Or(Field field, double marker)
    {
        if (field.Value.Data.TryGetAsF64(out double value))
        {
            return value;
        }

        return marker;
    }

    private static string _TextOr(Field field, string marker)
    {
        if (field.TryGetCustomText(out LazyString text) && !text.IsNull)
        {
            return (string)text;
        }

        return marker;
    }

    private static string _StringOr(Field field, string marker)
    {
        if (field.Value.Data.TryGetAsString(out string? value))
        {
            return value;
        }

        return marker;
    }

    protected static List<T> One<T>(T value) => [value];

    #endregion
}
