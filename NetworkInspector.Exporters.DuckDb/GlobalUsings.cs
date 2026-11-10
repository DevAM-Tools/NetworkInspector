// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region Formatting
global using static InvariantStringHelper.InvariantText;
#endregion

#region System
global using System;
global using System.Collections.Concurrent;
global using System.Collections.Generic;
global using System.Globalization;
global using System.IO;
global using System.Text;
global using System.Threading;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Errors;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Exporters
global using NetworkInspector.Exporters;
global using NetworkInspector.Exporters.Columnar;
global using NetworkInspector.Exporters.Content;
global using NetworkInspector.Exporters.Writing;
#endregion

#region External Dependencies
global using DuckDB.NET.Data;
#endregion
