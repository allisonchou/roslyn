// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using Roslyn.Utilities;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.Remote
{
    internal class EditorConfigLanguageServer : ServiceBase
    {
        public EditorConfigLanguageServer(Stream stream, IServiceProvider serviceProvider)
            : base(serviceProvider, stream, SpecializedCollections.EmptyEnumerable<JsonConverter>())
        {
            StartService();
        }

        [JsonRpcMethod(Methods.InitializeName)]
        public object Initialize(int? processId, string rootPath, Uri rootUri, ClientCapabilities capabilities, TraceSetting trace, CancellationToken cancellationToken)
        {
            return new InitializeResult()
            {
                Capabilities = new ServerCapabilities()
                {
                    CodeActionProvider = true
                }
            };
        }

        [JsonRpcMethod(Methods.InitializedName)]
        public Task Initialized()
        {
            return Task.CompletedTask;
        }

        [JsonRpcMethod(Methods.TextDocumentCodeActionName)]
        public Task<object[]> AddMissingRulesCodeAction(CodeActionParams parameters, CancellationToken cancellationToken)
        {
            // to-do: change this
            return null;
        }

        [JsonRpcMethod(Methods.ShutdownName)]
        public void Shutdown(CancellationToken cancellationToken)
        {
            // our language server shutdown when VS shutdown
            // we have this so that we don't get log file every time VS shutdown
        }

        [JsonRpcMethod(Methods.ExitName)]
        public void Exit()
        {
            // our language server exit when VS shutdown
            // we have this so that we don't get log file every time VS shutdown
        }
    }
}
