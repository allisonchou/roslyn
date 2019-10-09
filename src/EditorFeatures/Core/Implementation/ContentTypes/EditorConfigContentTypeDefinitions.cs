// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.ContentTypes
{
    internal static class EditorConfigContentTypeDefinitions
    {
        /// <summary>
        /// Definition of a content type that is a base definition for all content types supported by Roslyn.
        /// </summary>
        public const string Name = "EditorConfig";

        [Export]
        [ContentType(Name)]
        [FileExtension(".editorconfig")]
        public static readonly FileExtensionToContentTypeDefinition EditorConfigFileExtensionMapping = null;

        [Export]
        [Name(Name)]
        [BaseDefinition("code")]
        public static readonly ContentTypeDefinition EditorConfigContentTypeDefinition = null;
    }
}
