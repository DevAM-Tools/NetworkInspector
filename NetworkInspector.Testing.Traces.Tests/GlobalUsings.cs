// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Collections.Generic;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Security.Cryptography;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region TUnit
global using TUnit.Assertions;
global using TUnit.Assertions.Enums;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

#region NetworkInspector
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Plugins;
global using NetworkInspector.Protocols;
global using NetworkInspector.Protocols.SignalMessage;
global using NetworkInspector.Sources.Pcapng;
global using NetworkInspector.Sources.Random;
global using NetworkInspector.Testing.Traces;
global using NetworkInspector.Values;
#endregion

#region Sample plugin
global using Sample.Protocol;
#endregion
