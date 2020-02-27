﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.ChangeSignature
{
    internal interface IUnifiedArgumentSyntax
    {
        bool IsDefault { get; }
        bool IsNamed { get; }
        string GetName();

        /// <summary>The original index of the argument in the list.</summary>
        int Index { get; }

        IUnifiedArgumentSyntax WithName(string name);
        IUnifiedArgumentSyntax WithAdditionalAnnotations(SyntaxAnnotation annotation);
    }
}
