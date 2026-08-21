// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.IO;
global using System.IO.Compression;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Text;
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

#region NetworkInspector.Protocols
global using NetworkInspector.Protocols;
#endregion

#region NetworkInspector.Sources
global using NetworkInspector.Sources.Asc;
global using NetworkInspector.Sources.Asc.Format;
global using NetworkInspector.Sources.Blf;
global using NetworkInspector.Sources.Blf.Format;
global using NetworkInspector.Sources.Blf.Format.Headers;
global using NetworkInspector.Sources.Blf.Format.Objects;
global using NetworkInspector.Sources.Cached;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Pcapng.Format;
global using NetworkInspector.Sources.Random;
global using NetworkInspector.Sources.Tests.Generators;
global using NetworkInspector.Sources.Tests.Helpers;
#endregion

#region ZeroAlloc
global using ZeroAlloc;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion
