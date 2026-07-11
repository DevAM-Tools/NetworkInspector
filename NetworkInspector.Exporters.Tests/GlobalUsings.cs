// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.IO;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters;
global using NetworkInspector.Exporters.Asc;
global using NetworkInspector.Exporters.Blf;
global using NetworkInspector.Exporters.Csv;
global using NetworkInspector.Exporters.Json;
global using NetworkInspector.Exporters.Pbf;
global using NetworkInspector.Exporters.Pcapng;
global using NetworkInspector.Exporters.Tests;
global using NetworkInspector.Exporters.Tests.Generators;
global using NetworkInspector.Exporters.Tests.Verification;
global using NetworkInspector.Testing.Tshark;
global using NetworkInspector.Exporters.Text;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Blf;
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Blf.Format.Headers;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Pcapng.Format;
#endregion

#region ZeroAlloc
global using ZeroAlloc;
#endregion

#region NetworkInspector.Exporters (internal)
global using NetworkInspector.Exporters.Pbf.Columnar;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

