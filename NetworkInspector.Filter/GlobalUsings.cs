// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region System
global using System;
global using System.Buffers.Binary;
global using System.Collections.Generic;
global using System.Diagnostics.CodeAnalysis;
global using System.Globalization;
global using System.Linq.Expressions;
global using System.Runtime.CompilerServices;
global using System.Text;
global using System.Text.RegularExpressions;
#endregion

#region NetworkInspector.Core
global using NetworkInspector.Core;
global using NetworkInspector.Core.Fields;
global using NetworkInspector.Core.Ids;
global using NetworkInspector.Core.Index;
global using NetworkInspector.Core.Infos;
global using NetworkInspector.Core.Interfaces;
global using NetworkInspector.Values;
#endregion

#region NetworkInspector.Filter
global using NetworkInspector.Filter.Analysis;
global using NetworkInspector.Filter.Ast;
global using NetworkInspector.Filter.Errors;
global using NetworkInspector.Filter.Eval;
global using NetworkInspector.Filter.Jit;
global using NetworkInspector.Filter.Lexer;
global using NetworkInspector.Filter.Parser;
global using NetworkInspector.Filter.Scope;
global using NetworkInspector.Filter.Stateful;
#endregion
