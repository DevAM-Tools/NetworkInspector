// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.IO;
global using System.Reflection;
global using System.Threading;
global using System.Threading.Tasks;
global using static System.FormattableString;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
#endregion

#region NetworkInspector.Plugins and Protocols
global using NetworkInspector.Plugins;
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Asc;
global using NetworkInspector.Sources.Blf;
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Pcapng.Format;
global using NetworkInspector.Sources.Random;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters.Asc;
global using NetworkInspector.Exporters.Blf;
global using NetworkInspector.Exporters.Csv;
global using NetworkInspector.Exporters.DuckDb;
global using NetworkInspector.Exporters.Json;
global using NetworkInspector.Exporters.Parquet;
global using NetworkInspector.Exporters.Pbf;
global using NetworkInspector.Exporters.Pcapng;
global using NetworkInspector.Exporters.Text;
#endregion
