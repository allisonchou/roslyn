using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.CodeFixes.Suppression
{
    internal sealed class ConfigureSeverityLevelCodeAction : CodeAction.CodeActionWithNestedActions
    {
        public ConfigureSeverityLevelCodeAction(
            Diagnostic diagnostic,
            ImmutableArray<CodeAction> nestedActions)
            : base("Configure severity level",
            ImmutableArray<CodeAction>.CastUp(nestedActions), isInlinable: false)
        {
        }

        internal static Task<Solution> ConfigureEditorConfig(
            string severity,
            Diagnostic diagnostic,
            IEnumerable<TextDocument> additionalFiles,
            Solution solution,
            Dictionary<string, Option<CodeStyleOption<bool>>> languageSpecificOptions,
            Dictionary<string, Option<CodeStyleOption<ExpressionBodyPreference>>> expressionOptions,
            string language,
            CancellationToken cancellationToken)
        {
            // Finding .editorconfig if it exists as an additional file in project
            TextDocument editorconfig = null;
            foreach (var document in additionalFiles)
            {
                if (document.Name.Equals(".editorconfig"))
                {
                    editorconfig = document;
                }
            }

            if (editorconfig != null)
            {
                editorconfig.TryGetText(out var text);
                var result = editorconfig.GetTextAsync().Result;

                var headers = new Dictionary<string, TextSpan>();
                string mostRecentHeader = null;
                if (diagnosticToEditorConfigDotNet.ContainsKey(diagnostic.Id) ||
                    languageSpecificOptions.ContainsKey(diagnostic.Id) ||
                    (expressionOptions != null && expressionOptions.ContainsKey(diagnostic.Id)) ||
                    (diagnostic.Properties != null && diagnostic.Properties.ContainsKey("OptionName") && diagnostic.Properties.ContainsKey("OptionCurrent")))
                {
                    // Find the name of the rule
                    var name = "";
                    if (diagnosticToEditorConfigDotNet.ContainsKey(diagnostic.Id))
                    {
                        diagnosticToEditorConfigDotNet.TryGetValue(diagnostic.Id, out var value);
                        var storageLocation = value.StorageLocations.OfType<EditorConfigStorageLocation<CodeStyleOption<bool>>>().FirstOrDefault();
                        if (storageLocation != null)
                        {
                            name = storageLocation.KeyName;
                        }

                    }
                    else if (languageSpecificOptions.ContainsKey(diagnostic.Id))
                    {
                        languageSpecificOptions.TryGetValue(diagnostic.Id, out var value);
                        var storageLocation = value.StorageLocations.OfType<EditorConfigStorageLocation<CodeStyleOption<bool>>>().FirstOrDefault();
                        if (storageLocation != null)
                        {
                            name = storageLocation.KeyName;
                        }
                    }
                    else if (expressionOptions != null && expressionOptions.ContainsKey(diagnostic.Id))
                    {
                        expressionOptions.TryGetValue(diagnostic.Id, out var value);
                        var storageLocation = value.StorageLocations.OfType<EditorConfigStorageLocation<CodeStyleOption<ExpressionBodyPreference>>>().FirstOrDefault();
                        if (storageLocation != null)
                        {
                            name = storageLocation.KeyName;
                        }
                    }
                    else if (diagnostic.Properties != null && diagnostic.Properties.ContainsKey("OptionName"))
                    {
                        diagnostic.Properties.TryGetValue("OptionName", out name);
                    }

                    // First check if given .editorconfig rule is already in file
                    var lines = result.Lines;
                    if (name.Length != 0)
                    {
                        foreach (var curLine in lines)
                        {
                            var curLineText = curLine.ToString();

                            var rulePattern = new Regex(@"([\w ]+)=([\w ]+):([\w ]+)");
                            var headerPattern = new Regex(@"\[\*([^#\[]*)\]");
                            if (rulePattern.IsMatch(curLineText))
                            {
                                var groups = rulePattern.Match(curLineText).Groups;
                                var ruleName = groups[1].Value.ToString().Trim();

                                var validRule = true;
                                if (mostRecentHeader != null &&
                                    (language == LanguageNames.CSharp && !mostRecentHeader.Contains("cs") ||
                                    language == LanguageNames.VisualBasic && !mostRecentHeader.Contains("vb")))
                                {
                                    validRule = false;
                                }

                                // We found the rule in the file -- replace it
                                if (name.Equals(ruleName) && validRule)
                                {
                                    var textChange = new TextChange(curLine.Span, ruleName + " = " + groups[2].Value.ToString().Trim() + ":" + severity);
                                    return Task.FromResult(solution.WithAdditionalDocumentText(editorconfig.Id, result.WithChanges(textChange)));
                                }
                            }
                            else if (headerPattern.IsMatch(curLineText.Trim()))
                            {
                                var groups = headerPattern.Match(curLineText.Trim()).Groups;
                                mostRecentHeader = groups[1].Value.ToString();
                                headers.Add(groups[1].Value.ToString().ToLowerInvariant(), curLine.Span);
                            }
                        }
                    }

                    // If we reach this point, no match was found, so we add the rule to the .editorconfig file
                    var option = "";
                    if (diagnosticToEditorConfigDotNet.ContainsKey(diagnostic.Id))
                    {
                        diagnosticToEditorConfigDotNet.TryGetValue(diagnostic.Id, out var value);
                        option = solution.Workspace.Options.GetOption(value, language).Value.ToString().ToLowerInvariant();
                    }
                    else if (languageSpecificOptions.ContainsKey(diagnostic.Id))
                    {
                        languageSpecificOptions.TryGetValue(diagnostic.Id, out var value);
                        option = solution.Workspace.Options.GetOption(value).Value.ToString().ToLowerInvariant();
                    }
                    else if (expressionOptions != null && expressionOptions.ContainsKey(diagnostic.Id))
                    {
                        expressionOptions.TryGetValue(diagnostic.Id, out var value);
                        var preference = solution.Workspace.Options.GetOption(value).Value;
                        if (preference == ExpressionBodyPreference.WhenPossible)
                        {
                            option = "true";
                        }
                        else if (preference == ExpressionBodyPreference.WhenOnSingleLine)
                        {
                            option = "when_on_single_line";
                        }
                        else if (preference == ExpressionBodyPreference.Never)
                        {
                            option = "false";
                        }
                    }
                    else if (diagnostic.Properties != null && diagnostic.Properties.ContainsKey("OptionCurrent"))
                    {
                        diagnostic.Properties.TryGetValue("OptionCurrent", out option);
                        option = option.ToLowerInvariant();
                    }

                    if (name.Length != 0 && option.Length != 0)
                    {
                        var newRule = name + " = " + option + ":" + severity;

                        // Insert correct header if applicable
                        if (language == LanguageNames.CSharp && headers.Where(header => header.Key.Contains(".cs")).Count() != 0)
                        {
                            var csheader = headers.Where(header => header.Key.Contains(".cs"));
                            var textChange = new TextChange(new TextSpan(csheader.FirstOrDefault().Value.End, 0), "\r\n" + newRule);
                            solution = solution.WithAdditionalDocumentText(editorconfig.Id, result.WithChanges(textChange));
                        }
                        else if (language == LanguageNames.VisualBasic && headers.Where(header => header.Key.Contains(".vb")).Count() != 0)
                        {
                            var vbheader = headers.Where(header => header.Key.Contains(".vb"));
                            var textChange = new TextChange(new TextSpan(vbheader.FirstOrDefault().Value.End, 0), "\r\n" + newRule);
                            solution = solution.WithAdditionalDocumentText(editorconfig.Id, result.WithChanges(textChange));
                        }
                        else if (language == LanguageNames.CSharp || language == LanguageNames.VisualBasic)
                        {
                            var newRuleWithHeader = "";

                            // Insert a newline if not already present
                            var lastLine = lines.AsEnumerable().LastOrDefault();
                            if (lastLine.ToString().Trim().Length != 0)
                            {
                                newRuleWithHeader = "\r\n";
                            }

                            if (language == LanguageNames.CSharp)
                            {
                                newRuleWithHeader += "[*.cs]\r\n" + newRule;
                            }
                            else if (language == LanguageNames.VisualBasic)
                            {
                                newRuleWithHeader += "[*.vb]\r\n" + newRule;
                            }
                            var textChange = new TextChange(new TextSpan(result.Length, 0), newRuleWithHeader);
                            solution = solution.WithAdditionalDocumentText(editorconfig.Id, result.WithChanges(textChange));
                        }
                    }

                    return Task.FromResult(solution);
                }
            }
            return Task.FromResult(solution);
        }

        internal static readonly Dictionary<string, PerLanguageOption<CodeStyleOption<bool>>> diagnosticToEditorConfigDotNet = new Dictionary<string, Options.PerLanguageOption<CodeStyleOption<bool>>>()
        {
            // Dictionary(error code, editorconfig name)
            { IDEDiagnosticIds.UseThrowExpressionDiagnosticId, CodeStyleOptions.PreferThrowExpression },
            { IDEDiagnosticIds.UseObjectInitializerDiagnosticId, CodeStyleOptions.PreferObjectInitializer },
            { IDEDiagnosticIds.InlineDeclarationDiagnosticId, CodeStyleOptions.PreferInlinedVariableDeclaration },
            { IDEDiagnosticIds.UseCollectionInitializerDiagnosticId, CodeStyleOptions.PreferCollectionInitializer },
            { IDEDiagnosticIds.UseCoalesceExpressionDiagnosticId, CodeStyleOptions.PreferCoalesceExpression },
            { IDEDiagnosticIds.UseCoalesceExpressionForNullableDiagnosticId, CodeStyleOptions.PreferCoalesceExpression },
            { IDEDiagnosticIds.UseNullPropagationDiagnosticId, CodeStyleOptions.PreferNullPropagation },
            { IDEDiagnosticIds.UseAutoPropertyDiagnosticId, CodeStyleOptions.PreferAutoProperties },
            { IDEDiagnosticIds.UseExplicitTupleNameDiagnosticId, CodeStyleOptions.PreferExplicitTupleNames },
            { IDEDiagnosticIds.UseDeconstructionDiagnosticId, CodeStyleOptions.PreferDeconstructedVariableDeclaration },
            { IDEDiagnosticIds.MakeFieldReadonlyDiagnosticId, CodeStyleOptions.PreferReadonly },
            { IDEDiagnosticIds.UseConditionalExpressionForAssignmentDiagnosticId, CodeStyleOptions.PreferConditionalExpressionOverAssignment },
            { IDEDiagnosticIds.UseConditionalExpressionForReturnDiagnosticId, CodeStyleOptions.PreferConditionalExpressionOverReturn }
        };
    }
}
