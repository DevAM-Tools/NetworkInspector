// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// The signal message of the CAN trace in two forms from one description: the FrameBuilder <see cref="Layout"/> that encodes
/// the data bytes, and the JSON configuration file (<see cref="WriteConfig"/>) that the signal message protocol reads. The
/// fixture holds the vehicle dynamics message only. Static; thread-safe.
/// </summary>
public static class SignalConfigFixture
{
    #region Constants

    /// <summary>Local name of the vehicle speed signal in the layout.</summary>
    internal const string SpeedName = "VehicleSpeed";

    /// <summary>Local name of the gear signal in the layout.</summary>
    internal const string GearName = "Gear";

    /// <summary>Local name of the wheel speed signal in the layout (dotted on purpose).</summary>
    internal const string WheelSpeedName = "ChassisControlUnit_Front.WheelSpeedSensorFrontLeft.FilteredWheelSpeedValue_kph";

    /// <summary>Length of the message in bytes.</summary>
    internal const int ByteLength = 8;

    private const string _CanIdTable = "can.id";

    #endregion

    #region Properties

    /// <summary>The message layout: three signals and the dispatch binding of the CAN identifier.</summary>
    public static SignalMessageLayout Layout { get; } = new()
    {
        PduId = CanSignalTrace.CanId,
        Name = CanSignalTrace.MessageName,
        UiName = "Vehicle dynamics",
        ByteLength = ByteLength,
        Signals = ImmutableArray.Create(
            new SignalSpec(SpeedName, "Vehicle speed", 0, 8, SignalEndian.Little, 1.0, 0.0, "km/h"),
            new SignalSpec(GearName, "Gear", 8, 3, SignalEndian.Little, 1.0, 0.0, string.Empty),
            new SignalSpec(WheelSpeedName, "Wheel speed front left", 16, 16, SignalEndian.Little, 0.01, 0.0, "km/h")),
        DispatchBindings = ImmutableArray.Create(new DispatchBinding(_CanIdTable, CanSignalTrace.CanId)),
        Mux = null,
        MuxGroups = default,
    };

    #endregion

    #region Public API

    /// <summary>Writes the signal message configuration JSON of <see cref="Layout"/>, with the value names of speed and gear.</summary>
    /// <param name="path">The file to write (replaced when it exists).</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or white space (<see cref="ArgumentNullException"/> when null).</exception>
    public static void WriteConfig(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Layout is the single description: the JSON is derived from it field by field, so the encoded bytes (layout) and the
        // decoded values (configuration read by the protocol) cannot drift apart. FileMode.Create replaces a leftover file of
        // an earlier write. Indented output only helps humans reading the file.
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("messages");
        writer.WriteStartObject();
        writer.WriteString("name", Layout.Name);
        writer.WriteString("ui_name", Layout.UiName);
        writer.WriteNumber("byte_length", Layout.ByteLength);

        writer.WriteStartArray("dispatch_bindings");
        foreach (DispatchBinding binding in Layout.DispatchBindings)
        {
            writer.WriteStartObject();
            writer.WriteString("table", binding.Table);
            writer.WriteNumber("key", binding.Key);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        // The parser takes the field name as it is registered, so the message name is put in front of the local signal name.
        writer.WriteStartArray("signals");
        foreach (SignalSpec signal in Layout.Signals)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Layout.Name + "." + signal.Name);
            writer.WriteString("ui_name", signal.UiName);
            writer.WriteNumber("start_bit", signal.StartBit);
            writer.WriteNumber("bit_length", signal.BitLength);
            writer.WriteString("byte_order", signal.Endian == SignalEndian.Little ? "little_endian" : "big_endian");
            writer.WriteNumber("factor", signal.Factor);
            writer.WriteNumber("offset", signal.Offset);
            writer.WriteString("unit", signal.Unit);
            _WriteValueNames(writer, signal.Name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    #endregion

    #region Private Helpers

    /// <summary>Writes the value names of speed (the two special values) and gear (the five gears).</summary>
    private static void _WriteValueNames(Utf8JsonWriter writer, string signalName)
    {
        if (signalName == SpeedName)
        {
            writer.WriteStartObject("value_names");
            writer.WriteString("254", "ERROR");
            writer.WriteString("255", "SNA");
            writer.WriteEndObject();
        }
        else if (signalName == GearName)
        {
            writer.WriteStartObject("value_names");
            writer.WriteString("0", "P");
            writer.WriteString("1", "R");
            writer.WriteString("2", "N");
            writer.WriteString("3", "D");
            writer.WriteString("4", "S");
            writer.WriteEndObject();
        }
    }

    #endregion
}
