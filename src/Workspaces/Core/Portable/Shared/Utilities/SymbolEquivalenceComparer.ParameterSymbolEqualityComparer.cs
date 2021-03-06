﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Shared.Utilities
{
    internal partial class SymbolEquivalenceComparer
    {
        internal class ParameterSymbolEqualityComparer : IEqualityComparer<IParameterSymbol>
        {
            private readonly SymbolEquivalenceComparer symbolEqualityComparer;

            public ParameterSymbolEqualityComparer(
                SymbolEquivalenceComparer symbolEqualityComparer)
            {
                this.symbolEqualityComparer = symbolEqualityComparer;
            }

            public bool Equals(
                IParameterSymbol x,
                IParameterSymbol y,
                Dictionary<INamedTypeSymbol, INamedTypeSymbol> equivalentTypesWithDifferingAssemblies,
                bool compareParameterName,
                bool isCaseSensitive)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x == null || y == null)
                {
                    return false;
                }

                var nameComparisonCheck = true;
                if (compareParameterName)
                {
                    nameComparisonCheck = isCaseSensitive ?
                        x.Name == y.Name
                        : string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                }

                // See the comment in the outer type.  If we're comparing two parameters for
                // equality, then we want to consider method type parameters by index only.

                return
                    x.RefKind == y.RefKind &&
                    nameComparisonCheck &&
                    symbolEqualityComparer.SignatureTypeEquivalenceComparer.Equals(x.Type, y.Type, equivalentTypesWithDifferingAssemblies);
            }

            public bool Equals(IParameterSymbol x, IParameterSymbol y)
            {
                return this.Equals(x, y, null, false, false);
            }

            public bool Equals(IParameterSymbol x, IParameterSymbol y, bool compareParameterName, bool isCaseSensitive)
            {
                return this.Equals(x, y, null, compareParameterName, isCaseSensitive);
            }

            public int GetHashCode(IParameterSymbol x)
            {
                if (x == null)
                {
                    return 0;
                }

                return
                    Hash.Combine(x.IsRefOrOut(),
                    symbolEqualityComparer.SignatureTypeEquivalenceComparer.GetHashCode(x.Type));
            }
        }
    }
}
