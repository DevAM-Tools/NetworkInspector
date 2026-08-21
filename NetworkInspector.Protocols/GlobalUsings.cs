// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Buffers.Text;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Runtime.Intrinsics;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Text.Json.Serialization.Metadata;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Cache;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Core.Reassembly;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Core.Tables;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols.Attributes;
global using NetworkInspector.Protocols.Can;
global using NetworkInspector.Protocols.Dns;
global using NetworkInspector.Protocols.Dtls;
global using NetworkInspector.Protocols.Helpers;
global using NetworkInspector.Protocols.Http2;
global using NetworkInspector.Protocols.Icmpv6;
global using NetworkInspector.Protocols.PduTransport;
global using NetworkInspector.Protocols.SignalMessage;
global using NetworkInspector.Protocols.SomeIp;
global using NetworkInspector.Protocols.Tcp;
global using NetworkInspector.Protocols.Tls;
global using NetworkInspector.Protocols.Udp;
global using NetworkInspector.Protocols.WebSocket;
#endregion

#region External Dependencies
global using ZeroAlloc;
global using static NetworkInspector.Protocols.ZA;
#endregion
