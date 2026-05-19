// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
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
global using NetworkInspector.Protocols.Helpers;
#endregion

#region External Dependencies
global using ZeroAlloc;
global using static NetworkInspector.Protocols.ZA;
#endregion
