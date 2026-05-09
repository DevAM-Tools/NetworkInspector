// Copyright (c) DevAM and Network Inspector Contributors
// Licensed under the MIT license.

namespace NetworkInspector.Core;

/// <summary>
/// Pcap link-layer header types (DLT values).
/// Maps to libpcap's <c>LINKTYPE_*</c> / <c>DLT_*</c> constants.
/// See <see href="https://www.tcpdump.org/linktypes.html"/> for the canonical registry.
/// </summary>
public enum LinkType
{
    #region Enum Values

    // ── Core Link Types (0–10) ───────────────────────────────────────

    /// <summary>BSD loopback encapsulation (DLT_NULL).</summary>
    Null = 0,
    /// <summary>IEEE 802.3 Ethernet (DLT_EN10MB).</summary>
    Ethernet = 1,
    /// <summary>Experimental Ethernet 3Mb (DLT_EN3MB).</summary>
    ExpEthernet = 2,
    /// <summary>AX.25 packet radio (DLT_AX25).</summary>
    Ax25 = 3,
    /// <summary>ProNET Token Ring (DLT_PRONET).</summary>
    Pronet = 4,
    /// <summary>MIT Chaosnet (DLT_CHAOS).</summary>
    Chaos = 5,
    /// <summary>IEEE 802.5 Token Ring (DLT_IEEE802).</summary>
    Ieee8025 = 6,
    /// <summary>ARCNET BSD-style (DLT_ARCNET).</summary>
    ArcnetBsd = 7,
    /// <summary>SLIP with direction header (DLT_SLIP).</summary>
    Slip = 8,
    /// <summary>PPP (DLT_PPP).</summary>
    Ppp = 9,
    /// <summary>FDDI (DLT_FDDI).</summary>
    Fddi = 10,

    // ── PPP Variants (50–51) ─────────────────────────────────────────

    /// <summary>PPP in HDLC-like framing (DLT_PPP_SERIAL).</summary>
    PppHdlc = 50,
    /// <summary>PPP over Ethernet (DLT_PPP_ETHER).</summary>
    PppEther = 51,

    // ── ATM, Raw IP, Cisco (99–109) ─────────────────────────────────

    /// <summary>Symantec Enterprise Firewall (DLT_SYMANTEC_FIREWALL).</summary>
    SymantecFirewall = 99,
    /// <summary>RFC 1483 LLC/SNAP-encapsulated ATM (DLT_ATM_RFC1483).</summary>
    AtmRfc1483 = 100,
    /// <summary>Raw IP (DLT_RAW).</summary>
    Raw = 101,
    /// <summary>SLIP BSD/OS (DLT_SLIP_BSDOS).</summary>
    SlipBsdos = 102,
    /// <summary>PPP BSD/OS (DLT_PPP_BSDOS).</summary>
    PppBsdos = 103,
    /// <summary>Cisco HDLC (DLT_C_HDLC).</summary>
    CHdlc = 104,
    /// <summary>IEEE 802.11 wireless LAN (DLT_IEEE802_11).</summary>
    Ieee80211 = 105,
    /// <summary>ATM CLIP, Linux (DLT_ATM_CLIP).</summary>
    AtmClip = 106,
    /// <summary>Frame Relay (DLT_FRELAY).</summary>
    Frelay = 107,
    /// <summary>OpenBSD loopback (DLT_LOOP).</summary>
    Loop = 108,
    /// <summary>IPsec encapsulation (DLT_ENC).</summary>
    Enc = 109,

    // ── Cooked Captures and Specialized (113–127) ───────────────────

    /// <summary>Linux cooked capture v1 (DLT_LINUX_SLL).</summary>
    LinuxSll = 113,
    /// <summary>Apple LocalTalk (DLT_LTALK).</summary>
    Ltalk = 114,
    /// <summary>OpenBSD pflog (DLT_PFLOG).</summary>
    Pflog = 117,
    /// <summary>Prism monitor mode 802.11 (DLT_PRISM_HEADER).</summary>
    Ieee80211Prism = 119,
    /// <summary>IP over Fibre Channel (DLT_IP_OVER_FC).</summary>
    IpOverFc = 122,
    /// <summary>SunATM (DLT_SUNATM).</summary>
    SunAtm = 123,
    /// <summary>802.11 with radiotap header (DLT_IEEE802_11_RADIO).</summary>
    Ieee80211Radiotap = 127,

    // ── ARCNET and Apple (129–138) ──────────────────────────────────

    /// <summary>ARCNET Linux-style (DLT_ARCNET_LINUX).</summary>
    ArcnetLinux = 129,
    /// <summary>Apple IP over IEEE 1394 (DLT_APPLE_IP_OVER_IEEE1394).</summary>
    AppleIpOverIeee1394 = 138,

    // ── SS7 and DOCSIS (139–144) ────────────────────────────────────

    /// <summary>SS7 MTP2 with pseudo-header (DLT_MTP2_WITH_PHDR).</summary>
    Mtp2WithPhdr = 139,
    /// <summary>SS7 MTP2 (DLT_MTP2).</summary>
    Mtp2 = 140,
    /// <summary>SS7 MTP3 (DLT_MTP3).</summary>
    Mtp3 = 141,
    /// <summary>SS7 SCCP (DLT_SCCP).</summary>
    Sccp = 142,
    /// <summary>DOCSIS MAC frames (DLT_DOCSIS).</summary>
    Docsis = 143,
    /// <summary>Linux IrDA (DLT_LINUX_IRDA).</summary>
    LinuxIrda = 144,

    // ── User-defined Private Use (147–162) ──────────────────────────

    /// <summary>User 0, private use (DLT_USER0).</summary>
    User0 = 147,
    /// <summary>User 1, private use (DLT_USER1).</summary>
    User1 = 148,
    /// <summary>User 2, private use (DLT_USER2).</summary>
    User2 = 149,
    /// <summary>User 3, private use (DLT_USER3).</summary>
    User3 = 150,
    /// <summary>User 4, private use (DLT_USER4).</summary>
    User4 = 151,
    /// <summary>User 5, private use (DLT_USER5).</summary>
    User5 = 152,
    /// <summary>User 6, private use (DLT_USER6).</summary>
    User6 = 153,
    /// <summary>User 7, private use (DLT_USER7).</summary>
    User7 = 154,
    /// <summary>User 8, private use (DLT_USER8).</summary>
    User8 = 155,
    /// <summary>User 9, private use (DLT_USER9).</summary>
    User9 = 156,
    /// <summary>User 10, private use (DLT_USER10).</summary>
    User10 = 157,
    /// <summary>User 11, private use (DLT_USER11).</summary>
    User11 = 158,
    /// <summary>User 12, private use (DLT_USER12).</summary>
    User12 = 159,
    /// <summary>User 13, private use (DLT_USER13).</summary>
    User13 = 160,
    /// <summary>User 14, private use (DLT_USER14).</summary>
    User14 = 161,
    /// <summary>User 15, private use (DLT_USER15).</summary>
    User15 = 162,

    // ── 802.11 AVS and BACnet (163–165) ─────────────────────────────

    /// <summary>IEEE 802.11 with AVS header (DLT_IEEE802_11_RADIO_AVS).</summary>
    Ieee80211Avs = 163,
    /// <summary>BACnet MS/TP (DLT_BACNET_MS_TP).</summary>
    BacnetMsTp = 165,

    // ── PPP with Direction and GPRS (166–171) ───────────────────────

    /// <summary>PPP with direction (DLT_PPP_PPPD).</summary>
    PppPppd = 166,
    /// <summary>GPRS LLC (DLT_GPRS_LLC).</summary>
    GprsLlc = 169,
    /// <summary>GPF-T transparent (DLT_GPF_T).</summary>
    GpfT = 170,
    /// <summary>GPF-F frame-mapped (DLT_GPF_F).</summary>
    GpfF = 171,

    // ── LAPD, USB, Bluetooth, 802.16, CAN, 802.15.4, ERF (177–197) ─

    /// <summary>Linux LAPD (DLT_LINUX_LAPD).</summary>
    LinuxLapd = 177,
    /// <summary>USB FreeBSD (DLT_USB_FREEBSD).</summary>
    UsbFreebsd = 186,
    /// <summary>Bluetooth HCI H4 (DLT_BLUETOOTH_HCI_H4).</summary>
    BluetoothHciH4 = 187,
    /// <summary>IEEE 802.16 MAC CPS (DLT_IEEE802_16_MAC_CPS).</summary>
    Ieee80216MacCps = 188,
    /// <summary>USB packets with Linux header (DLT_USB_LINUX).</summary>
    UsbLinux = 189,
    /// <summary>CAN 2.0B (DLT_CAN_SOCKETCAN).</summary>
    Can20B = 190,
    /// <summary>IEEE 802.15.4, Linux (DLT_IEEE802_15_4_LINUX).</summary>
    Ieee802154Linux = 191,
    /// <summary>Per-Packet Information (DLT_PPI).</summary>
    Ppi = 192,
    /// <summary>IEEE 802.16 MAC CPS with radio header (DLT_IEEE802_16_MAC_CPS_RADIO).</summary>
    Ieee80216MacCpsRadio = 193,
    /// <summary>IEEE 802.15.4 with FCS (DLT_IEEE802_15_4_WITHFCS).</summary>
    Ieee802154WithFcs = 195,
    /// <summary>SITA (DLT_SITA).</summary>
    Sita = 196,
    /// <summary>Endace ERF (DLT_ERF).</summary>
    Erf = 197,

    // ── Bluetooth, AX.25, Direction Variants (201–207) ──────────────

    /// <summary>Bluetooth HCI H4 with direction (DLT_BLUETOOTH_HCI_H4_WITH_PHDR).</summary>
    BluetoothHciH4WithPhdr = 201,
    /// <summary>AX.25 with KISS header (DLT_AX25_KISS).</summary>
    Ax25Kiss = 202,
    /// <summary>LAPD (DLT_LAPD).</summary>
    Lapd = 203,
    /// <summary>PPP with direction (DLT_PPP_WITH_DIR).</summary>
    PppWithDir = 204,
    /// <summary>Cisco HDLC with direction (DLT_C_HDLC_WITH_DIR).</summary>
    CHdlcWithDir = 205,
    /// <summary>Frame Relay with direction (DLT_FRELAY_WITH_DIR).</summary>
    FrelayWithDir = 206,
    /// <summary>LAPB with direction (DLT_LAPB_WITH_DIR).</summary>
    LapbWithDir = 207,

    // ── I2C, FlexRay, Automotive (209–215) ──────────────────────────

    /// <summary>I2C/IPMB, Linux (DLT_I2C_LINUX).</summary>
    I2cLinux = 209,
    /// <summary>FlexRay automotive bus (DLT_FLEXRAY).</summary>
    Flexray = 210,
    /// <summary>MOST automotive bus (DLT_MOST).</summary>
    Most = 211,
    /// <summary>LIN automotive bus (DLT_LIN).</summary>
    Lin = 212,
    /// <summary>X2E Serial (DLT_X2E_SERIAL).</summary>
    X2eSerial = 213,
    /// <summary>X2E Xoraya (DLT_X2E_XORAYA).</summary>
    X2eXoraya = 214,
    /// <summary>IEEE 802.15.4 Non-ASK PHY (DLT_IEEE802_15_4_NONASK_PHY).</summary>
    Ieee802154NonAskPhy = 215,

    // ── Linux Events, GSM, MPLS, USB (216–220) ─────────────────────

    /// <summary>Linux evdev events (DLT_LINUX_EVDEV).</summary>
    LinuxEvdev = 216,
    /// <summary>GSM Um interface, GSMTAP (DLT_GSMTAP_UM).</summary>
    GsmtapUm = 217,
    /// <summary>GSM Abis interface, GSMTAP (DLT_GSMTAP_ABIS).</summary>
    GsmtapAbis = 218,
    /// <summary>MPLS (DLT_MPLS).</summary>
    Mpls = 219,
    /// <summary>USB packets with Linux memory-mapped header (DLT_USB_LINUX_MMAPPED).</summary>
    UsbLinuxMmapped = 220,

    // ── DECT, Space, Fibre Channel, Solaris (221–229) ───────────────

    /// <summary>DECT (DLT_DECT).</summary>
    Dect = 221,
    /// <summary>AOS Space Data Link Protocol (DLT_AOS).</summary>
    Aos = 222,
    /// <summary>WirelessHART (DLT_WIHART).</summary>
    Wihart = 223,
    /// <summary>Fibre Channel FC-2 (DLT_FC_2).</summary>
    Fc2 = 224,
    /// <summary>Fibre Channel FC-2 with frame delimiters (DLT_FC_2_WITH_FRAME_DELIMS).</summary>
    Fc2WithFrameDelims = 225,
    /// <summary>Solaris ipnet (DLT_IPNET).</summary>
    Ipnet = 226,
    /// <summary>SocketCAN (DLT_CAN_SOCKETCAN).</summary>
    CanSocketcan = 227,
    /// <summary>Raw IPv4 (DLT_IPV4).</summary>
    IPv4 = 228,
    /// <summary>Raw IPv6 (DLT_IPV6).</summary>
    IPv6 = 229,

    // ── IEEE 802.15.4, D-Bus, DVB (230–237) ────────────────────────

    /// <summary>IEEE 802.15.4 without FCS (DLT_IEEE802_15_4_NOFCS).</summary>
    Ieee802154NoFcs = 230,
    /// <summary>D-Bus messages (DLT_DBUS).</summary>
    Dbus = 231,
    /// <summary>DVB-CI (DLT_DVB_CI).</summary>
    DvbCi = 235,
    /// <summary>MUX 27.010 (DLT_MUX27010).</summary>
    Mux27010 = 236,
    /// <summary>STANAG 5066 D_PDU (DLT_STANAG_5066_D_PDU).</summary>
    Stanag5066DPdu = 237,

    // ── NFLOG, Network Analyzers, InfiniBand (239–248) ──────────────

    /// <summary>Linux netfilter log messages (DLT_NFLOG).</summary>
    Nflog = 239,
    /// <summary>Hilscher netANALYZER (DLT_NETANALYZER).</summary>
    Netanalyzer = 240,
    /// <summary>Hilscher netANALYZER transparent (DLT_NETANALYZER_TRANSPARENT).</summary>
    NetanalyzerTransparent = 241,
    /// <summary>IP over InfiniBand (DLT_IPOIB).</summary>
    Ipoib = 242,
    /// <summary>MPEG-2 Transport Stream (DLT_MPEG_2_TS).</summary>
    Mpeg2Ts = 243,
    /// <summary>NFC LLCP (DLT_NFC_LLCP).</summary>
    NfcLlcp = 245,
    /// <summary>InfiniBand (DLT_INFINIBAND).</summary>
    Infiniband = 247,
    /// <summary>SCTP (DLT_SCTP).</summary>
    Sctp = 248,

    // ── USB, Bluetooth LE, Netlink (249–256) ────────────────────────

    /// <summary>USBPcap (DLT_USBPCAP).</summary>
    Usbpcap = 249,
    /// <summary>RTAC Serial (DLT_RTAC_SERIAL).</summary>
    RtacSerial = 250,
    /// <summary>Bluetooth Low Energy Link Layer (DLT_BLUETOOTH_LE_LL).</summary>
    BluetoothLeLl = 251,
    /// <summary>Wireshark Upper PDU export (DLT_WIRESHARK_UPPER_PDU).</summary>
    WiresharkUpperPdu = 252,
    /// <summary>Linux Netlink (DLT_NETLINK).</summary>
    Netlink = 253,
    /// <summary>Bluetooth Linux Monitor (DLT_BLUETOOTH_LINUX_MONITOR).</summary>
    BluetoothLinuxMonitor = 254,
    /// <summary>Bluetooth BR/EDR Baseband (DLT_BLUETOOTH_BREDR_BB).</summary>
    BluetoothBredrBb = 255,
    /// <summary>Bluetooth LE Link Layer with RF info (DLT_BLUETOOTH_LE_LL_WITH_PHDR).</summary>
    BluetoothLeLlWithPhdr = 256,

    // ── PROFIBUS, PKTAP, EPON (257–262) ─────────────────────────────

    /// <summary>PROFIBUS Data Link (DLT_PROFIBUS_DL).</summary>
    ProfibusDl = 257,
    /// <summary>Apple PKTAP (DLT_PKTAP).</summary>
    Pktap = 258,
    /// <summary>Ethernet with preamble (DLT_EPON).</summary>
    Epon = 259,
    /// <summary>IPMI HPM.2 (DLT_IPMI_HPM_2).</summary>
    IpmiHpm2 = 260,
    /// <summary>Z-Wave R1/R2 (DLT_ZWAVE_R1_R2).</summary>
    ZwaveR1R2 = 261,
    /// <summary>Z-Wave R3 (DLT_ZWAVE_R3).</summary>
    ZwaveR3 = 262,

    // ── IoT and Smartcard (263–266) ─────────────────────────────────

    /// <summary>WattStopper DLM (DLT_WATTSTOPPER_DLM).</summary>
    WattstopperDlm = 263,
    /// <summary>ISO 14443 contactless smartcard (DLT_ISO_14443).</summary>
    Iso14443 = 264,
    /// <summary>Radio Data System (DLT_RDS).</summary>
    Rds = 265,
    /// <summary>USB Darwin/macOS (DLT_USB_DARWIN).</summary>
    UsbDarwin = 266,

    // ── OpenFlow, SDLC, LoRa, vSock (267–272) ──────────────────────

    /// <summary>OpenFlow (DLT_OPENFLOW).</summary>
    Openflow = 267,
    /// <summary>SDLC (DLT_SDLC).</summary>
    Sdlc = 268,
    /// <summary>TI LLN Sniffer (DLT_TI_LLN_SNIFFER).</summary>
    TiLlnSniffer = 269,
    /// <summary>LoRaTap (DLT_LORATAP).</summary>
    Loratap = 270,
    /// <summary>vSock (DLT_VSOCK).</summary>
    Vsock = 271,
    /// <summary>Nordic Semiconductor BLE (DLT_NORDIC_BLE).</summary>
    NordicBle = 272,

    // ── DOCSIS 3.1, Ethernet mPacket, DisplayPort (273–276) ────────

    /// <summary>DOCSIS 3.1 XRA-31 (DLT_DOCSIS31_XRA31).</summary>
    Docsis31Xra31 = 273,
    /// <summary>Ethernet mPacket (DLT_ETHERNET_MPACKET).</summary>
    EthernetMpacket = 274,
    /// <summary>DisplayPort AUX channel (DLT_DISPLAYPORT_AUX).</summary>
    DisplayportAux = 275,
    /// <summary>Linux cooked capture v2 (DLT_LINUX_SLL2).</summary>
    LinuxSll2 = 276,

    // ── Sercos, USB Sniffers, VPP (277–280) ─────────────────────────

    /// <summary>Sercos Monitor (DLT_SERCOS_MONITOR).</summary>
    SercosMonitor = 277,
    /// <summary>OpenVizsla USB sniffer (DLT_OPENVIZSLA).</summary>
    Openvizsla = 278,
    /// <summary>Elektrobit EBHSCR (DLT_EBHSCR).</summary>
    Ebhscr = 279,
    /// <summary>VPP dispatch trace (DLT_VPP_DISPATCH).</summary>
    VppDispatch = 280,

    // ── DSA Tags, IEEE 802.15.4 TAP (281–285) ──────────────────────

    /// <summary>DSA tag, Broadcom (DLT_DSA_TAG_BRCM).</summary>
    DsaTagBrcm = 281,
    /// <summary>DSA tag, Broadcom prepend (DLT_DSA_TAG_BRCM_PREPEND).</summary>
    DsaTagBrcmPrepend = 282,
    /// <summary>IEEE 802.15.4 TAP (DLT_IEEE802_15_4_TAP).</summary>
    Ieee802154Tap = 283,
    /// <summary>DSA tag, Marvell DSA (DLT_DSA_TAG_DSA).</summary>
    DsaTagDsa = 284,
    /// <summary>DSA tag, Marvell EDSA (DLT_DSA_TAG_EDSA).</summary>
    DsaTagEdsa = 285,

    // ── Z-Wave Serial, USB 2.0, ATSC (287–295) ─────────────────────

    /// <summary>Z-Wave Serial API (DLT_ZWAVE_SERIAL).</summary>
    ZwaveSerial = 287,
    /// <summary>USB 2.0 (DLT_USB_2_0).</summary>
    Usb20 = 288,
    /// <summary>ATSC ALP (DLT_ATSC_ALP).</summary>
    AtscAlp = 289,
    /// <summary>Event Tracing for Windows (DLT_ETW).</summary>
    Etw = 290,
    /// <summary>ZBOSS NCP (DLT_ZBOSS_NCP).</summary>
    ZbossNcp = 292,
    /// <summary>USB 2.0 Low Speed (DLT_USB_2_0_LOW_SPEED).</summary>
    Usb20LowSpeed = 293,
    /// <summary>USB 2.0 Full Speed (DLT_USB_2_0_FULL_SPEED).</summary>
    Usb20FullSpeed = 294,
    /// <summary>USB 2.0 High Speed (DLT_USB_2_0_HIGH_SPEED).</summary>
    Usb20HighSpeed = 295,

    // ── Auerswald, Z-Wave TAP, Silicon Labs, FiRa (296–301) ────────

    /// <summary>Auerswald Logger (DLT_AUERSWALD_LOG).</summary>
    AuerswaldLog = 296,
    /// <summary>Z-Wave TAP (DLT_ZWAVE_TAP).</summary>
    ZwaveTap = 297,
    /// <summary>Silicon Labs Debug Channel (DLT_SILABS_DEBUG_CHANNEL).</summary>
    SilabsDebugChannel = 298,
    /// <summary>FiRa UCI (DLT_FIRA_UCI).</summary>
    FiraUci = 299,
    /// <summary>MDB (DLT_MDB).</summary>
    Mdb = 300,
    /// <summary>DECT NR (DLT_DECT_NR).</summary>
    DectNr = 301,

    #endregion
}

/// <summary>Extension methods for <see cref="LinkType"/>.</summary>
public static class LinkTypeExtensions
{
    #region Extension Methods
    /// <summary>Returns a human-readable display name for this link type.</summary>
    public static string UiName(this LinkType linkType) => linkType switch
    {
        // Core Link Types
        LinkType.Null => "Null/Loopback",
        LinkType.Ethernet => "Ethernet",
        LinkType.ExpEthernet => "Experimental Ethernet (3Mb)",
        LinkType.Ax25 => "AX.25",
        LinkType.Pronet => "ProNET Token Ring",
        LinkType.Chaos => "Chaosnet",
        LinkType.Ieee8025 => "IEEE 802.5 Token Ring",
        LinkType.ArcnetBsd => "ARCNET (BSD)",
        LinkType.Slip => "SLIP",
        LinkType.Ppp => "PPP",
        LinkType.Fddi => "FDDI",

        // PPP Variants
        LinkType.PppHdlc => "PPP (HDLC)",
        LinkType.PppEther => "PPPoE",

        // ATM, Raw IP, Cisco
        LinkType.SymantecFirewall => "Symantec Firewall",
        LinkType.AtmRfc1483 => "ATM RFC1483",
        LinkType.Raw => "Raw IP",
        LinkType.SlipBsdos => "SLIP (BSD/OS)",
        LinkType.PppBsdos => "PPP (BSD/OS)",
        LinkType.CHdlc => "Cisco HDLC",
        LinkType.Ieee80211 => "IEEE 802.11 Wireless",
        LinkType.AtmClip => "ATM CLIP (Linux)",
        LinkType.Frelay => "Frame Relay",
        LinkType.Loop => "OpenBSD Loopback",
        LinkType.Enc => "IPsec Encapsulation",

        // Cooked Captures and Specialized
        LinkType.LinuxSll => "Linux Cooked Capture v1",
        LinkType.Ltalk => "Apple LocalTalk",
        LinkType.Pflog => "OpenBSD pflog",
        LinkType.Ieee80211Prism => "IEEE 802.11 + Prism",
        LinkType.IpOverFc => "IP over Fibre Channel",
        LinkType.SunAtm => "SunATM",
        LinkType.Ieee80211Radiotap => "IEEE 802.11 + Radiotap",

        // ARCNET and Apple
        LinkType.ArcnetLinux => "ARCNET (Linux)",
        LinkType.AppleIpOverIeee1394 => "Apple IP over IEEE 1394",

        // SS7 and DOCSIS
        LinkType.Mtp2WithPhdr => "SS7 MTP2 with Pseudo-Header",
        LinkType.Mtp2 => "SS7 MTP2",
        LinkType.Mtp3 => "SS7 MTP3",
        LinkType.Sccp => "SS7 SCCP",
        LinkType.Docsis => "DOCSIS",
        LinkType.LinuxIrda => "Linux IrDA",

        // User-defined Private Use
        LinkType.User0 => "User 0 (Private)",
        LinkType.User1 => "User 1 (Private)",
        LinkType.User2 => "User 2 (Private)",
        LinkType.User3 => "User 3 (Private)",
        LinkType.User4 => "User 4 (Private)",
        LinkType.User5 => "User 5 (Private)",
        LinkType.User6 => "User 6 (Private)",
        LinkType.User7 => "User 7 (Private)",
        LinkType.User8 => "User 8 (Private)",
        LinkType.User9 => "User 9 (Private)",
        LinkType.User10 => "User 10 (Private)",
        LinkType.User11 => "User 11 (Private)",
        LinkType.User12 => "User 12 (Private)",
        LinkType.User13 => "User 13 (Private)",
        LinkType.User14 => "User 14 (Private)",
        LinkType.User15 => "User 15 (Private)",

        // 802.11 AVS and BACnet
        LinkType.Ieee80211Avs => "IEEE 802.11 + AVS",
        LinkType.BacnetMsTp => "BACnet MS/TP",

        // PPP with Direction and GPRS
        LinkType.PppPppd => "PPP with Direction",
        LinkType.GprsLlc => "GPRS LLC",
        LinkType.GpfT => "GPF-T (Transparent)",
        LinkType.GpfF => "GPF-F (Frame-mapped)",

        // LAPD, USB, Bluetooth, CAN, 802.15.4, ERF
        LinkType.LinuxLapd => "Linux LAPD",
        LinkType.UsbFreebsd => "USB (FreeBSD)",
        LinkType.BluetoothHciH4 => "Bluetooth HCI H4",
        LinkType.Ieee80216MacCps => "IEEE 802.16 MAC CPS",
        LinkType.UsbLinux => "USB (Linux)",
        LinkType.Can20B => "CAN 2.0B",
        LinkType.Ieee802154Linux => "IEEE 802.15.4 (Linux)",
        LinkType.Ppi => "Per-Packet Information",
        LinkType.Ieee80216MacCpsRadio => "IEEE 802.16 MAC CPS + Radio",
        LinkType.Ieee802154WithFcs => "IEEE 802.15.4 with FCS",
        LinkType.Sita => "SITA",
        LinkType.Erf => "Endace ERF",

        // Bluetooth, AX.25, Direction Variants
        LinkType.BluetoothHciH4WithPhdr => "Bluetooth HCI H4 with Direction",
        LinkType.Ax25Kiss => "AX.25 with KISS",
        LinkType.Lapd => "LAPD",
        LinkType.PppWithDir => "PPP with Direction",
        LinkType.CHdlcWithDir => "Cisco HDLC with Direction",
        LinkType.FrelayWithDir => "Frame Relay with Direction",
        LinkType.LapbWithDir => "LAPB with Direction",

        // I2C, FlexRay, Automotive
        LinkType.I2cLinux => "I2C/IPMB (Linux)",
        LinkType.Flexray => "FlexRay",
        LinkType.Most => "MOST",
        LinkType.Lin => "LIN",
        LinkType.X2eSerial => "X2E Serial",
        LinkType.X2eXoraya => "X2E Xoraya",
        LinkType.Ieee802154NonAskPhy => "IEEE 802.15.4 (Non-ASK PHY)",

        // Linux Events, GSM, MPLS, USB
        LinkType.LinuxEvdev => "Linux evdev",
        LinkType.GsmtapUm => "GSM Um (GSMTAP)",
        LinkType.GsmtapAbis => "GSM Abis (GSMTAP)",
        LinkType.Mpls => "MPLS",
        LinkType.UsbLinuxMmapped => "USB (Linux, mmapped)",

        // DECT, Space, Fibre Channel, Solaris
        LinkType.Dect => "DECT",
        LinkType.Aos => "AOS Space Data Link",
        LinkType.Wihart => "WirelessHART",
        LinkType.Fc2 => "Fibre Channel FC-2",
        LinkType.Fc2WithFrameDelims => "Fibre Channel FC-2 (with delimiters)",
        LinkType.Ipnet => "Solaris ipnet",
        LinkType.CanSocketcan => "SocketCAN",
        LinkType.IPv4 => "Raw IPv4",
        LinkType.IPv6 => "Raw IPv6",

        // IEEE 802.15.4, D-Bus, DVB
        LinkType.Ieee802154NoFcs => "IEEE 802.15.4 (no FCS)",
        LinkType.Dbus => "D-Bus",
        LinkType.DvbCi => "DVB-CI",
        LinkType.Mux27010 => "MUX 27.010",
        LinkType.Stanag5066DPdu => "STANAG 5066 D_PDU",

        // NFLOG, Network Analyzers, InfiniBand
        LinkType.Nflog => "Linux NFLOG",
        LinkType.Netanalyzer => "netANALYZER",
        LinkType.NetanalyzerTransparent => "netANALYZER (transparent)",
        LinkType.Ipoib => "IP over InfiniBand",
        LinkType.Mpeg2Ts => "MPEG-2 TS",
        LinkType.NfcLlcp => "NFC LLCP",
        LinkType.Infiniband => "InfiniBand",
        LinkType.Sctp => "SCTP",

        // USB, Bluetooth LE, Netlink
        LinkType.Usbpcap => "USBPcap",
        LinkType.RtacSerial => "RTAC Serial",
        LinkType.BluetoothLeLl => "Bluetooth LE Link Layer",
        LinkType.WiresharkUpperPdu => "Wireshark Upper PDU",
        LinkType.Netlink => "Linux Netlink",
        LinkType.BluetoothLinuxMonitor => "Bluetooth Linux Monitor",
        LinkType.BluetoothBredrBb => "Bluetooth BR/EDR Baseband",
        LinkType.BluetoothLeLlWithPhdr => "Bluetooth LE LL with RF Info",

        // PROFIBUS, PKTAP, EPON
        LinkType.ProfibusDl => "PROFIBUS Data Link",
        LinkType.Pktap => "Apple PKTAP",
        LinkType.Epon => "Ethernet with Preamble",
        LinkType.IpmiHpm2 => "IPMI HPM.2",
        LinkType.ZwaveR1R2 => "Z-Wave R1/R2",
        LinkType.ZwaveR3 => "Z-Wave R3",

        // IoT and Smartcard
        LinkType.WattstopperDlm => "WattStopper DLM",
        LinkType.Iso14443 => "ISO 14443 Smartcard",
        LinkType.Rds => "Radio Data System",
        LinkType.UsbDarwin => "USB (Darwin/macOS)",

        // OpenFlow, SDLC, LoRa, vSock
        LinkType.Openflow => "OpenFlow",
        LinkType.Sdlc => "SDLC",
        LinkType.TiLlnSniffer => "TI LLN Sniffer",
        LinkType.Loratap => "LoRaTap",
        LinkType.Vsock => "vSock",
        LinkType.NordicBle => "Nordic BLE",

        // DOCSIS 3.1, Ethernet mPacket, DisplayPort, Linux SLL2
        LinkType.Docsis31Xra31 => "DOCSIS 3.1 (XRA-31)",
        LinkType.EthernetMpacket => "Ethernet mPacket",
        LinkType.DisplayportAux => "DisplayPort AUX",
        LinkType.LinuxSll2 => "Linux Cooked Capture v2",

        // Sercos, USB Sniffers, VPP
        LinkType.SercosMonitor => "Sercos Monitor",
        LinkType.Openvizsla => "OpenVizsla",
        LinkType.Ebhscr => "EBHSCR",
        LinkType.VppDispatch => "VPP Dispatch",

        // DSA Tags, IEEE 802.15.4 TAP
        LinkType.DsaTagBrcm => "DSA Tag (Broadcom)",
        LinkType.DsaTagBrcmPrepend => "DSA Tag (Broadcom Prepend)",
        LinkType.Ieee802154Tap => "IEEE 802.15.4 (TAP)",
        LinkType.DsaTagDsa => "DSA Tag (Marvell DSA)",
        LinkType.DsaTagEdsa => "DSA Tag (Marvell EDSA)",

        // Z-Wave Serial, USB 2.0, ATSC
        LinkType.ZwaveSerial => "Z-Wave Serial",
        LinkType.Usb20 => "USB 2.0",
        LinkType.AtscAlp => "ATSC ALP",
        LinkType.Etw => "Event Tracing for Windows",
        LinkType.ZbossNcp => "ZBOSS NCP",
        LinkType.Usb20LowSpeed => "USB 2.0 (Low Speed)",
        LinkType.Usb20FullSpeed => "USB 2.0 (Full Speed)",
        LinkType.Usb20HighSpeed => "USB 2.0 (High Speed)",

        // Auerswald, Z-Wave TAP, Silicon Labs, FiRa, MDB, DECT NR
        LinkType.AuerswaldLog => "Auerswald Logger",
        LinkType.ZwaveTap => "Z-Wave (TAP)",
        LinkType.SilabsDebugChannel => "Silicon Labs Debug",
        LinkType.FiraUci => "FiRa UCI",
        LinkType.Mdb => "MDB",
        LinkType.DectNr => "DECT NR",

        _ => linkType.ToString(),
    };

    #endregion
}
