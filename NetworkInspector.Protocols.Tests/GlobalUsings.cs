// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using System.Threading.Tasks;
global using System.Xml.Linq;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Reassembly;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Values;
global using ZeroAlloc;
#endregion

#region NetworkInspector.FrameBuilder
global using FrameDispatchBinding = NetworkInspector.FrameBuilder.DispatchBinding;
global using NetworkInspector.FrameBuilder;
global using NetworkInspector.FrameBuilder.Constants;
global using NetworkInspector.FrameBuilder.Core;
global using NetworkInspector.FrameBuilder.Headers;
global using NetworkInspector.Protocols.Tests.Infrastructure;
global using NetworkInspector.Testing.Tshark;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
global using NetworkInspector.Protocols.Can;
global using NetworkInspector.Protocols.Dns;
global using NetworkInspector.Protocols.Http2;
global using NetworkInspector.Protocols.Icmpv6;
global using NetworkInspector.Protocols.IPv4;
global using NetworkInspector.Protocols.PduTransport;
global using NetworkInspector.Protocols.SignalMessage;
global using NetworkInspector.Protocols.Tcp;
global using NetworkInspector.Protocols.Tests.Infrastructure.Bridges;
global using NetworkInspector.Protocols.Tests.Infrastructure.TsharkUat;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion
