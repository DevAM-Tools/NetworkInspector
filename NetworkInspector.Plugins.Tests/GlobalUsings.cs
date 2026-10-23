// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Reflection.Metadata;
global using System.Reflection.PortableExecutable;
global using System.Runtime.ExceptionServices;
global using System.Runtime.InteropServices;
global using System.Runtime.Loader;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using static System.FormattableString;
#endregion

#region Roslyn (test assembly compiler)
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.CodeAnalysis.Emit;
// Roslyn and Core both define ParseOptions; the tests mean the Core type.
global using ParseOptions = NetworkInspector.Core.ParseOptions;
#endregion

#region TUnit
global using TUnit.Assertions;
global using TUnit.Assertions.Enums;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
global using TUnit.Core.Interfaces;
#endregion

#region NetworkInspector
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Plugins;
global using NetworkInspector.Plugins.Loader;
global using NetworkInspector.Protocols;
global using NetworkInspector.Values;
#endregion

#region Built-in plugins, sources, and exporters
global using NetworkInspector.BuiltInPlugins;
global using NetworkInspector.Exporters.Asc;
global using NetworkInspector.Exporters.Blf;
global using NetworkInspector.Exporters.Csv;
global using NetworkInspector.Exporters.DuckDb;
global using NetworkInspector.Exporters.Json;
global using NetworkInspector.Exporters.Parquet;
global using NetworkInspector.Exporters.Pbf;
global using NetworkInspector.Exporters.Pcapng;
global using NetworkInspector.Exporters.Text;
global using NetworkInspector.Sources.Asc;
global using NetworkInspector.Sources.Blf;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Random;
#endregion

#region Sample plugin
global using Sample.Protocol;
#endregion

#region External Dependencies
global using ZeroAlloc;
#endregion
