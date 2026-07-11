// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
global using System.IO.MemoryMappedFiles;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Runtime.Intrinsics;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using Microsoft.Win32.SafeHandles;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Cache;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Protocols;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Asc.Format;
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Blf.Format.Headers;
global using NetworkInspector.Sources.Blf.Format.Objects;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Pcapng.Format;
global using NetworkInspector.Sources.Pcapng.Format.Blocks;
#endregion

#region External Dependencies
global using ZeroAlloc;
#endregion

