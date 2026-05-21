// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Diagnostics;
global using System.IO;
global using System.Runtime.CompilerServices;
global using System.Threading;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters;
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

#region NetworkInspector.Profiling
global using NetworkInspector.Profiling.Helpers;
global using NetworkInspector.Profiling.Scenarios;
#endregion
