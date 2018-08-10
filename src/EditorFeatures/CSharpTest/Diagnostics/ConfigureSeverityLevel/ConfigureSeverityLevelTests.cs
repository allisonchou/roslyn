using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.Editor.UnitTests.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Diagnostics.ConfigureSeverityLevel
{
    public partial class ConfigurationTestsBase : AbstractSuppressionDiagnosticTest
    {
        protected virtual override int CodeActionIndex => throw new NotImplementedException();

        protected override TestWorkspace CreateWorkspaceFromFile(string initialMarkup, TestParameters parameters)
        {
            throw new NotImplementedException();
        }

        protected override string GetLanguage()
        {
            throw new NotImplementedException();
        }

        protected override ParseOptions GetScriptOptions()
        {
            throw new NotImplementedException();
        }

        internal override Tuple<DiagnosticAnalyzer, ISuppressionFixProvider> CreateDiagnosticProviderAndFixer(Workspace workspace)
        {
            return new Tuple<DiagnosticAnalyzer, ISuppressionFixProvider>(
                        new UserDiagnosticAnalyzer(), new CSharpConfigure);
        }
        public class ErrorConfigurationTests : ConfigurationTestsBase
        {
            protected override int CodeActionIndex => 0;
        }
    }
}
