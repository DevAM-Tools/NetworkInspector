// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

namespace NetworkInspector.Core.Tests;

/// <summary>
/// Tests for <see cref="LinkTypeExtensions.UiName"/>.
/// Verifies a representative set of known link types, the full set of named enum values,
/// and the unknown fallback.
/// </summary>
internal sealed class LinkTypeExtensionsTests
{
    // === Representative spot-checks ===

    [Test]
    public async Task UiName_Ethernet() =>
        await Assert.That(LinkType.Ethernet.UiName()).IsEqualTo("Ethernet (IEEE 802.3)");

    [Test]
    public async Task UiName_Raw() =>
        await Assert.That(LinkType.Raw.UiName()).IsEqualTo("Raw IP");

    [Test]
    public async Task UiName_LinuxSll() =>
        await Assert.That(LinkType.LinuxSll.UiName()).IsEqualTo("Linux Cooked Capture v1");

    [Test]
    public async Task UiName_LinuxSll2() =>
        await Assert.That(LinkType.LinuxSll2.UiName()).IsEqualTo("Linux Cooked Capture v2");

    [Test]
    public async Task UiName_Null() =>
        await Assert.That(LinkType.Null.UiName()).IsEqualTo("Null/Loopback");

    [Test]
    public async Task UiName_Can20B() =>
        await Assert.That(LinkType.Can20B.UiName()).IsEqualTo("CAN 2.0B");

    [Test]
    public async Task UiName_CanSocketcan() =>
        await Assert.That(LinkType.CanSocketcan.UiName()).IsEqualTo("SocketCAN");

    [Test]
    public async Task UiName_Flexray() =>
        await Assert.That(LinkType.Flexray.UiName()).IsEqualTo("FlexRay");

    [Test]
    public async Task UiName_Lin() =>
        await Assert.That(LinkType.Lin.UiName()).IsEqualTo("LIN");

    [Test]
    public async Task UiName_BluetoothHciH4() =>
        await Assert.That(LinkType.BluetoothHciH4.UiName()).IsEqualTo("Bluetooth HCI H4");

    [Test]
    public async Task UiName_BluetoothLeLl() =>
        await Assert.That(LinkType.BluetoothLeLl.UiName()).IsEqualTo("Bluetooth LE Link Layer");

    [Test]
    public async Task UiName_Ieee80211() =>
        await Assert.That(LinkType.Ieee80211.UiName()).IsEqualTo("IEEE 802.11 Wireless");

    [Test]
    public async Task UiName_User0() =>
        await Assert.That(LinkType.User0.UiName()).IsEqualTo("User 0 (Private)");

    [Test]
    public async Task UiName_User15() =>
        await Assert.That(LinkType.User15.UiName()).IsEqualTo("User 15 (Private)");

    [Test]
    public async Task UiName_Usb20() =>
        await Assert.That(LinkType.Usb20.UiName()).IsEqualTo("USB 2.0");

    [Test]
    public async Task UiName_IPv4() =>
        await Assert.That(LinkType.IPv4.UiName()).IsEqualTo("Raw IPv4");

    [Test]
    public async Task UiName_IPv6() =>
        await Assert.That(LinkType.IPv6.UiName()).IsEqualTo("Raw IPv6");

    // === Completeness check: every named enum value must have an explicit switch case ===

    /// <summary>
    /// Iterates every named <see cref="LinkType"/> member and asserts that
    /// <see cref="LinkTypeExtensions.UiName"/> returns a human-readable label —
    /// i.e. not the raw C# identifier string produced by the default
    /// <c>_ => linkType.ToString()</c> fallback, and not a numeric string.
    /// This catches any future enum addition that forgets a matching switch case.
    /// </summary>
    [Test]
    public async Task UiName_AllNamedValues_ReturnHumanReadableName()
    {
        LinkType[] allValues = Enum.GetValues<LinkType>();
        List<string> missing = [];

        foreach (LinkType linkType in allValues)
        {
            string name = linkType.UiName();
            string rawIdentifier = linkType.ToString();

            // Guard 1: must not be empty.
            // Guard 2: must not be purely numeric (the fallback for unnamed int-casts).
            // Guard 3: must not equal the raw C# identifier name, because the fallback
            //          `_ => linkType.ToString()` returns the identifier for named members,
            //          which is not the human-readable label we require.
            if (string.IsNullOrEmpty(name)
                || long.TryParse(name, out _)
                || name == rawIdentifier)
            {
                missing.Add($"{linkType} ({(int)linkType}) -> UiName='{name}', identifier='{rawIdentifier}'");
            }
        }

        await Assert.That(missing).IsEmpty()
            .Because($"every named LinkType must have an explicit UiName switch case that differs from the raw identifier; missing or falling back: [{string.Join(", ", missing)}]");
    }

    // === Unknown / fallback ===

    [Test]
    public async Task UiName_Unknown_ReturnsEnumToString()
    {
        // Cast an invalid value — fallback is linkType.ToString(), which for a numeric-only
        // value produces the integer string.
        LinkType unknown = (LinkType)99999;
        string result = unknown.UiName();
        await Assert.That(result).IsEqualTo("99999");
    }
}