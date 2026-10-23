// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Testing.Traces;

/// <summary>
/// The names of the message and the fields that <see cref="CanSignalsRecipe"/> writes and <see cref="SignalConfigFixture"/>
/// describes, so that tests refer to them by constant instead of by copied text. Static; thread-safe.
/// </summary>
public static class CanSignalTrace
{
    /// <summary>SocketCAN identifier of the vehicle dynamics message (0x1A3).</summary>
    public const uint CanId = 0x1A3;

    /// <summary>Protocol name and container field of the message.</summary>
    public const string MessageName = "can.VehicleDynamics";

    /// <summary>Vehicle speed: 8 bits, factor 1, unit km/h; raw 254 means "error" and 255 "signal not available".</summary>
    public const string VehicleSpeedField = "can.VehicleDynamics.VehicleSpeed";

    /// <summary>Gear: 3 bits, enumeration P, R, N, D, S for the raw values 0 to 4.</summary>
    public const string GearField = "can.VehicleDynamics.Gear";

    /// <summary>A signal with a long dotted name: 16 bits little endian, factor 0.01, unit km/h.</summary>
    public const string WheelSpeedField =
        "can.VehicleDynamics.ChassisControlUnit_Front.WheelSpeedSensorFrontLeft.FilteredWheelSpeedValue_kph";

    /// <summary>The profile setting that names the signal configuration file.</summary>
    public const string ConfigFileSetting = "signal_message.config_file";
}
