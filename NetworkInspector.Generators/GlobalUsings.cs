// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

#region Formatting
// netstandard2.0 has no interpolated string handler overload: FormattableString.Invariant is the invariant form here.
global using static System.FormattableString;
#endregion

#region System
global using System;
global using System.Collections.Generic;
global using System.Collections.Immutable;
global using System.Globalization;
global using System.Linq;
global using System.Text;
#endregion

#region Microsoft.CodeAnalysis
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp.Syntax;
global using Microsoft.CodeAnalysis.Text;
#endregion

#region NetworkInspector.Generators
global using NetworkInspector.Generators.Models;
#endregion
