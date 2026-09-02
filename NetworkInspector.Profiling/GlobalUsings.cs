// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Core.ValueCaches;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters;
#endregion

#region NetworkInspector.Filter
global using NetworkInspector.Filter;
global using NetworkInspector.Filter.Errors;
// The namespace NetworkInspector.Filter shadows its own Filter type from inside other
// NetworkInspector assemblies, so the concrete filter needs an unambiguous alias.
global using PacketFilter = NetworkInspector.Filter.Filter;
#endregion

#region NetworkInspector.FrameBuilder
global using NetworkInspector.FrameBuilder;
global using NetworkInspector.FrameBuilder.Constants;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Blf;
global using NetworkInspector.Sources.Cached;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Random;
#endregion

#region NetworkInspector.Sessions
global using NetworkInspector.Sessions;
global using NetworkInspector.Sessions.Listeners;
global using NetworkInspector.Sessions.ValueCaches;
#endregion

#region NetworkInspector.Profiling
global using NetworkInspector.Profiling.Helpers;
global using NetworkInspector.Profiling.Scenarios;
#endregion
