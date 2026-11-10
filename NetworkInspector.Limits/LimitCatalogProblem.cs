// Copyright © 2026 DevAM. All rights reserved. Licensed under MIT license. See license in the repository root for license information.

namespace NetworkInspector.Limits;

/// <summary>One problem that <see cref="LimitCatalog.Check"/> found in a list of limit definitions.</summary>
/// <param name="Key">The duplicated key the problem is about.</param>
/// <param name="Message">What is wrong, in a sentence a maintainer can act on.</param>
/// <seealso cref="LimitCatalog"/>
public readonly record struct LimitCatalogProblem(string Key, string Message);
