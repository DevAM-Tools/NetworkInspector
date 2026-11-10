// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Runtime.Intrinsics;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.Json.Serialization.Metadata;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
// Static import so the marker singleton types (BoolMarker, I64Marker, ...) that FieldValueData uses as its type
// discriminant can be named without the FieldTypeMarkers prefix.
global using static NetworkInspector.Core.Fields.FieldTypeMarkers;
global using NetworkInspector.Core.Collections;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Limits;
global using NetworkInspector.Limits;
global using NetworkInspector.Limits.Utility;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Core.Reassembly;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Settings;
global using NetworkInspector.Core.Tables;
global using NetworkInspector.Core.ValueCaches;
global using NetworkInspector.Values;
#endregion

#region Formatting
global using static InvariantStringHelper.InvariantText;
#endregion
