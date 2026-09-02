// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Threading;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Collections;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.ValueCaches;
#endregion

#region NetworkInspector.Filter
global using NetworkInspector.Filter;
global using NetworkInspector.Filter.Errors;
// The namespace NetworkInspector.Filter shadows its own Filter type from inside
// NetworkInspector.Sessions, so the concrete filter needs an unambiguous alias.
global using PacketFilter = NetworkInspector.Filter.Filter;
#endregion

#region NetworkInspector.Sessions
global using NetworkInspector.Sessions.Cache;
global using NetworkInspector.Sessions.Ids;
global using NetworkInspector.Sessions.Jobs;
global using NetworkInspector.Sessions.Listeners;
global using NetworkInspector.Sessions.Sources;
global using NetworkInspector.Sessions.ValueCaches;
#endregion
