// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Collections.Generic;
global using System.Globalization;
global using System.IO;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region TUnit
global using TUnit.Assertions;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

#region NetworkInspector
global using NetworkInspector.Core;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Core.Settings;
global using NetworkInspector.Exporters.Blf;
global using NetworkInspector.Filter;
global using NetworkInspector.Filter.Errors;
global using NetworkInspector.Exporters.Json;
global using NetworkInspector.Exporters.Pbf;
global using NetworkInspector.Exporters.Text;
global using NetworkInspector.Protocols;
global using NetworkInspector.Sources.Random;
// The namespace NetworkInspector.Filter shadows its own Filter type, so the concrete filter
// needs an unambiguous alias.
global using PacketFilter = NetworkInspector.Filter.Filter;
#endregion

#region NetworkInspector.CLI
global using NetworkInspector.CLI;
global using NetworkInspector.CLI.Commands;
#endregion
