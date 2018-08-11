using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.CSharp.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.CSharp.UseCollectionInitializer;
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
        protected override int CodeActionIndex
        {
            get
            {
                return 0;
            }
        }

        protected override TestWorkspace CreateWorkspaceFromFile(string initialMarkup, TestParameters parameters)
            => TestWorkspace.CreateCSharp(initialMarkup, parameters.parseOptions, parameters.compilationOptions);

        protected override string GetLanguage() => LanguageNames.CSharp;

        protected override ParseOptions GetScriptOptions() => Options.Script;

        internal override Tuple<DiagnosticAnalyzer, ISuppressionFixProvider> CreateDiagnosticProviderAndFixer(Workspace workspace)
        {
            return new Tuple<DiagnosticAnalyzer, ISuppressionFixProvider>(
                        new CSharpUseCollectionInitializerDiagnosticAnalyzer(), new CSharpConfigureSeverityLevel());
        }

        public class NoneConfigurationTests : ConfigurationTestsBase
        {
            [Fact, Trait(Traits.Feature, Traits.Features.CodeActionsSuppression)]
            public async Task ConfigureEmptyEditorconfig()
            {
                var input = @"
<Workspace>
    <Project Language=""C#"" AssemblyName=""Assembly1"" CommonReferences=""true"">
        <Document>
class Program1
{
    static void Main()
    {
        // dotnet_style_object_initializer = true
        var c = new List<string>() { ""test"" };

        // dotnet_style_object_initializer = false
        var c2 = [|new List<string>()|];
        c2.Add(""test"");
    }
}
        </Document>
        <AdditionalDocument FilePath="".editorconfig"">
        </AdditionalDocument>
    </Project>
</Workspace>";

                var expected = @"
<Workspace>
    <Project Language=""C#"" AssemblyName=""Assembly1"" CommonReferences=""true"">
         <Document>
class Program1
{
    static void Main()
    {
        // dotnet_style_object_initializer = true
        var c = new List<string>() { ""test"" };

        // dotnet_style_object_initializer = false
        var c2 = [|new List<string>()|];
        c2.Add(""test"");
    }
}
        </Document>
        <AdditionalDocument FilePath="".editorconfig"">
[*.cs]
dotnet_style_object_initializer = true:none

        </AdditionalDocument>
    </Project>
</Workspace>";

            await TestInRegularAndScriptAsync(input, expected);
            }

            protected override int CodeActionIndex
            {
                get
                {
                    return 0;
                }
            }
        }
    }
}
