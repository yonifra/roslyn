﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Globalization;

namespace Microsoft.CodeAnalysis.CSharp
{
    public class CSharpDiagnosticFormatter : DiagnosticFormatter
    {
        internal CSharpDiagnosticFormatter()
        {
        }

        public new static readonly CSharpDiagnosticFormatter Instance = new CSharpDiagnosticFormatter();
    }
}
