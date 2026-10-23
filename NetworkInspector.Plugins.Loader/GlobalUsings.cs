// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.IO.Enumeration;
global using System.Linq;
global using System.Reflection;
global using System.Reflection.Metadata;
global using System.Reflection.PortableExecutable;
global using System.Runtime.Loader;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Threading;
global using System.Threading.Tasks;
global using static System.FormattableString;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Core.Reassembly;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Core.Tables;
#endregion

#region NetworkInspector.Plugins
global using NetworkInspector.Plugins;
#endregion
