// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.IO;
global using System.IO.Compression;
// Memory-mapped capture files (MmapPool): one read-only view serves the scan and all random-access readers.
global using System.IO.MemoryMappedFiles;
global using System.Numerics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
// Hardware intrinsics: used for the cache prefetch hint (CachePrefetch), which checks IsSupported before use and has a
// portable fallback, so no instruction set is assumed.
global using System.Runtime.Intrinsics;
global using System.Runtime.Intrinsics.X86;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using static System.FormattableString;
global using Microsoft.Win32.SafeHandles;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Cache;
global using NetworkInspector.Core.Collections;
global using NetworkInspector.Core.Concurrency;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Protocols
// Sources has no project reference to Protocols (it stays Core-only). This namespace resolves only because
// FlexRayLinkTypeFrame is link-compiled into this assembly as an internal type (see the project file).
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Asc.Format;
global using NetworkInspector.Sources.Collections;
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Blf.Format.Headers;
global using NetworkInspector.Sources.Blf.Format.Objects;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Pcapng.Format;
global using NetworkInspector.Sources.Pcapng.Format.Blocks;
#endregion

#region External Dependencies
// ZeroAlloc: provides the [BinaryParsable] attribute and the fixed-width endian field types (for example U32LE) that the
// packed on-disk header structs use (for example BlfLogObjectHeaderV2).
global using ZeroAlloc;
#endregion

