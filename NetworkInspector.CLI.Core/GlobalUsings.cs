// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Threading;
global using System.Threading.Tasks;
// Gives the commands Invariant($"..."): interpolated user-facing text is formatted with the invariant culture, never with the
// culture of the machine.
global using static InvariantStringHelper.InvariantText;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Limits;
global using NetworkInspector.Limits;
global using NetworkInspector.Limits.Utility;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Settings;
#endregion

#region NetworkInspector.Filter
global using NetworkInspector.Filter;
global using NetworkInspector.Filter.Errors;
// The namespace NetworkInspector.Filter shadows its own Filter type from inside other
// NetworkInspector assemblies, so the concrete filter needs an unambiguous alias.
global using PacketFilter = NetworkInspector.Filter.Filter;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Plugins
// The commands reach sources and export formats through the plugin registry (SourceConfig, FrameFormatConfig, PacketFormatConfig);
// the loader namespace supplies the plugin lifecycle and the export target rules.
global using NetworkInspector.BuiltInPlugins;
global using NetworkInspector.Plugins;
global using NetworkInspector.Plugins.Loader;
#endregion

#region NetworkInspector.CLI
global using NetworkInspector.CLI.Commands;
#endregion
