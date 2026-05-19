// Copyright © 2026 DevAM. Licensed under the MIT License. See LICENSE in the repository root.

#region System
global using System;
global using System.Buffers;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Globalization;
global using System.Linq;
global using System.Runtime.CompilerServices;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.FrameBuilder
global using NetworkInspector.FrameBuilder;
global using NetworkInspector.FrameBuilder.Constants;
global using NetworkInspector.FrameBuilder.Core;
global using NetworkInspector.FrameBuilder.Headers;
global using FB = NetworkInspector.FrameBuilder;
#endregion

#region NetworkInspector.Values
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Testing.Tshark
global using NetworkInspector.Testing.Tshark;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

#region External Dependencies
global using ZeroAlloc;
#endregion
