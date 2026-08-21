// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Buffers.Text;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Runtime.Intrinsics;
global using System.Text;
global using System.Threading;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters.Columnar;
global using NetworkInspector.Exporters.Pbf.Columnar;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Pcapng.Format;
#endregion

#region External Dependencies
global using Parquet;
global using Parquet.Schema;
global using ZeroAlloc;
// Parquet.Schema.Field collides with NetworkInspector.Core.Fields.Field; an alias directive
// takes priority over both using-namespace directives, so unqualified "Field" always resolves
// to the packet field-tree type used throughout this project (Parquet.Schema.Field is only ever
// referenced via its concrete subtype DataField, so it needs no unqualified name of its own).
global using Field = NetworkInspector.Core.Fields.Field;
#endregion

