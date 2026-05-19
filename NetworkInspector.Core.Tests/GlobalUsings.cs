// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#region System
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Text.Json.Serialization;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Index.ValueCache;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Reassembly;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Values;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

#region External Dependencies
global using ZeroAlloc;
#endregion
