// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Frozen;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Globalization;
global using System.IO;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector
global using NetworkInspector.Core;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Exporters.Pcapng;
global using NetworkInspector.FrameBuilder;
global using NetworkInspector.Sources.Random;
global using NetworkInspector.Values;
#endregion
