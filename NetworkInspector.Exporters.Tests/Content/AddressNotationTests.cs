// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

using AddressValues = NetworkInspector.Exporters.Tests.Helpers.AddressProbePacketFactory.Values;

namespace NetworkInspector.Exporters.Tests.Content;

/// <summary>
/// The text formats keep the readable notation of an address: CSV, JSON, and Text show <c>192.168.1.1</c>, <c>2001:DB8::1</c>,
/// <c>00:1A:2B:3C:4D:5E</c>, <c>00:1A:2B:FF:FE:3C:4D:5E</c>, and the UUID with dashes, and never the integer the binary formats
/// store. A reader of a text export needs no conversion; the integer forms exist only where a column has a numeric type.
/// </summary>
internal sealed class AddressNotationTests
{
    #region Running an export and checking the notation

    /// <summary>Exports the readable address packet to the exporter a delegate builds and returns the text it wrote.</summary>
    /// <param name="export">Builds the exporter on the stream, feeds it the packet, and finishes it.</param>
    /// <returns>The exporter's output decoded as UTF-8.</returns>
    private static string _Run(Action<MemoryStream, Packet> export)
    {
        using AddressProbePacketFactory factory = new();
        using MemoryStream stream = new();
        export(stream, factory.CreateTreePacket(AddressProbePacketFactory.Readable));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The factory content for a shape; the exporters of the tests below need the factory's own stack for selected fields.</summary>
    /// <param name="shape">The export shape (hierarchy or all fields; no selection is needed).</param>
    /// <returns>The content.</returns>
    private static ExportContent _Content(ExportShape shape) =>
        new() { Shape = shape, Info = false, FrameBytes = false };

    /// <summary>The integer texts the binary formats store for the readable values; none may appear in a text export.</summary>
    /// <returns>The decimal text of each integer form.</returns>
    private static string[] _IntegerForms()
    {
        AddressValues values = AddressProbePacketFactory.Readable;
        return
        [
            values.IPv4.RawValue.ToString(CultureInfo.InvariantCulture),
            values.Mac.RawValue.ToString(CultureInfo.InvariantCulture),
            values.Eui64.RawValue.ToString(CultureInfo.InvariantCulture),
            values.IPv6.High.ToString(CultureInfo.InvariantCulture),
            values.Uuid.High.ToString(CultureInfo.InvariantCulture),
            values.Uuid.Low.ToString(CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>Asserts that a text export shows every readable address and no integer form.</summary>
    /// <param name="text">The export's output.</param>
    /// <returns>A task that completes when every assertion has passed.</returns>
    private static async Task _AssertNotation(string text)
    {
        foreach (string notation in AddressProbePacketFactory.ReadableTexts)
        {
            await Assert.That(text).Contains(notation);
        }

        foreach (string integer in _IntegerForms())
        {
            await Assert.That(text).DoesNotContain(integer);
        }
    }

    /// <summary>Offers a packet that the exporter must accept; a refusal ends the test here.</summary>
    /// <param name="exporter">The exporter.</param>
    /// <param name="packet">The packet.</param>
    /// <exception cref="InvalidOperationException">The exporter refused the packet.</exception>
    private static void _Offer(IPacketListener exporter, Packet packet)
    {
        if (!exporter.OnPacket(packet))
        {
            throw new InvalidOperationException("The exporter refused the packet.");
        }
    }

    #endregion

    #region Formats

    /// <summary>Proves that the CSV all-fields export shows every address in its readable notation and no integer form.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task CsvAllFieldsShowsTheReadableNotation()
    {
        string text = _Run(static (stream, packet) =>
        {
            using CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithContent(_Content(ExportShape.AllFields)).Build();
            _Offer(exporter, packet);
            exporter.OnFinish();
        });

        await _AssertNotation(text);
    }

    /// <summary>Proves that the CSV hierarchy export shows every address in its readable notation and no integer form.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task CsvHierarchyShowsTheReadableNotation()
    {
        string text = _Run(static (stream, packet) =>
        {
            using CsvExporter exporter = CsvExporter.CreateBuilder().ToStream(stream).WithBom(false).WithContent(_Content(ExportShape.Hierarchy)).Build();
            _Offer(exporter, packet);
            exporter.OnFinish();
        });

        await _AssertNotation(text);
    }

    /// <summary>Proves that the JSON tree export shows every address in its readable notation in each JSON layout.</summary>
    /// <param name="format">The JSON layout under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(JsonExportFormat.Pretty)]
    [Arguments(JsonExportFormat.Array)]
    [Arguments(JsonExportFormat.Compact)]
    public async Task JsonTreeShowsTheReadableNotation(JsonExportFormat format)
    {
        string text = _Run((stream, packet) =>
        {
            using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithFormat(format).WithContent(_Content(ExportShape.Hierarchy)).Build();
            _Offer(exporter, packet);
            exporter.OnFinish();
        });

        await _AssertNotation(text);
    }

    /// <summary>Proves that the JSON all-fields export shows every address in its readable notation and no integer form.</summary>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    public async Task JsonAllFieldsShowsTheReadableNotation()
    {
        string text = _Run(static (stream, packet) =>
        {
            using JsonExporter exporter = JsonExporter.CreateBuilder().ToStream(stream).WithContent(_Content(ExportShape.AllFields)).Build();
            _Offer(exporter, packet);
            exporter.OnFinish();
        });

        await _AssertNotation(text);
    }

    /// <summary>Proves that the full-detail Text export shows every address in its readable notation for both shapes.</summary>
    /// <param name="shape">The export shape under test.</param>
    /// <returns>A task that completes when the assertions have passed.</returns>
    [Test]
    [Arguments(ExportShape.Hierarchy)]
    [Arguments(ExportShape.AllFields)]
    public async Task TextShowsTheReadableNotation(ExportShape shape)
    {
        string text = _Run((stream, packet) =>
        {
            using TextExporter exporter = TextExporter.CreateBuilder().ToStream(stream).WithDetailLevel(TextDetailLevel.Full)
                .WithContent(_Content(shape)).Build();
            _Offer(exporter, packet);
            exporter.OnFinish();
        });

        await _AssertNotation(text);
    }

    #endregion
}
