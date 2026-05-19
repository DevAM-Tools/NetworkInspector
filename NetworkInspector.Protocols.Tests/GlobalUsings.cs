// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Threading.Tasks;
global using System.Xml.Linq;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Values;
global using ZeroAlloc;
#endregion

#region NetworkInspector.FrameBuilder
global using NetworkInspector.FrameBuilder;
global using NetworkInspector.FrameBuilder.Constants;
global using NetworkInspector.FrameBuilder.Headers;
global using NetworkInspector.Protocols.Tests.Infrastructure;
global using NetworkInspector.Testing.Tshark;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion
