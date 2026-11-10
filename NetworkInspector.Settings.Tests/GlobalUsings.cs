// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers;
global using System.Collections;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.Json.Serialization;
global using System.Text.Json.Serialization.Metadata;
global using System.Threading;
global using System.Threading.Tasks;
#endregion

#region NetworkInspector.Settings
global using NetworkInspector.Limits;
global using NetworkInspector.Limits.Tests;
global using NetworkInspector.Limits.Utility;
global using NetworkInspector.Settings;
#endregion

#region Test Framework
global using TUnit.Assertions;
global using TUnit.Assertions.Enums;
global using TUnit.Assertions.Extensions;
global using TUnit.Core;
#endregion

#region Formatting
global using static InvariantStringHelper.InvariantText;
#endregion
