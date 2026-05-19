// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters;
global using NetworkInspector.Exporters.Blf;
global using NetworkInspector.Exporters.Json;
global using NetworkInspector.Exporters.Pbf;
global using NetworkInspector.Exporters.Pcapng;
global using NetworkInspector.Exporters.Text;
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

#region NetworkInspector.CLI
global using NetworkInspector.CLI.Commands;
#endregion
