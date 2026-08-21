// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.Linq;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Filter
global using NetworkInspector.Filter;
global using NetworkInspector.Filter.Errors;
// The namespace NetworkInspector.Filter shadows its own Filter type inside NetworkInspector.*.
global using PacketFilter = NetworkInspector.Filter.Filter;
#endregion

#region NetworkInspector.Sessions
global using NetworkInspector.Sessions;
global using NetworkInspector.Sessions.Cache;
global using NetworkInspector.Sessions.Ids;
global using NetworkInspector.Sessions.Jobs;
global using NetworkInspector.Sessions.Listeners;
global using NetworkInspector.Sessions.Sources;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region Test Framework
global using NetworkInspector.Sessions.Tests.Helpers;
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

