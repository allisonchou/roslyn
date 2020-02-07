﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.ChangeSignature;
using Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Formatting.Rules;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.ChangeSignature
{
    [ExportLanguageService(typeof(AbstractChangeSignatureService), LanguageNames.CSharp), Shared]
    internal sealed class CSharpChangeSignatureService : AbstractChangeSignatureService
    {
        private static readonly ImmutableArray<SyntaxKind> _declarationKinds = ImmutableArray.Create(
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.LocalFunctionStatement);

        private static readonly ImmutableArray<SyntaxKind> _declarationAndInvocableKinds =
            _declarationKinds.Concat(ImmutableArray.Create(
                SyntaxKind.InvocationExpression,
                SyntaxKind.ElementAccessExpression,
                SyntaxKind.ThisConstructorInitializer,
                SyntaxKind.BaseConstructorInitializer,
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.Attribute,
                SyntaxKind.NameMemberCref));

        private static readonly ImmutableArray<SyntaxKind> _updatableAncestorKinds = ImmutableArray.Create(
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.InvocationExpression,
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.ThisConstructorInitializer,
            SyntaxKind.BaseConstructorInitializer,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.Attribute,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.NameMemberCref);

        private static readonly ImmutableArray<SyntaxKind> _updatableNodeKinds = ImmutableArray.Create(
            SyntaxKind.MethodDeclaration,
            SyntaxKind.LocalFunctionStatement,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.InvocationExpression,
            SyntaxKind.ElementAccessExpression,
            SyntaxKind.ThisConstructorInitializer,
            SyntaxKind.BaseConstructorInitializer,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.Attribute,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.NameMemberCref,
            SyntaxKind.AnonymousMethodExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.SimpleLambdaExpression);

        [ImportingConstructor]
        public CSharpChangeSignatureService()
        {
        }

        public override async Task<(ISymbol symbol, int selectedIndex)> GetInvocationSymbolAsync(
            Document document, int position, bool restrictToDeclarations, CancellationToken cancellationToken)
        {
            var tree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

            var token = root.FindToken(position != tree.Length ? position : Math.Max(0, position - 1));

            // Allow the user to invoke Change-Sig if they've written:   Goo(a, b, c);$$ 
            if (token.Kind() == SyntaxKind.SemicolonToken && token.Parent is StatementSyntax)
            {
                token = token.GetPreviousToken();
                position = token.Span.End;
            }

            var matchingNode = GetMatchingNode(token.Parent, restrictToDeclarations);
            if (matchingNode == null)
            {
                return default;
            }

            // Don't show change-signature in the random whitespace/trivia for code.
            if (!matchingNode.Span.IntersectsWith(position))
            {
                return default;
            }

            // If we're actually on the declaration of some symbol, ensure that we're
            // in a good location for that symbol (i.e. not in the attributes/constraints).
            if (!InSymbolHeader(matchingNode, position))
            {
                return default;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var symbol = semanticModel.GetDeclaredSymbol(matchingNode, cancellationToken);
            if (symbol != null)
            {
                var selectedIndex = TryGetSelectedIndexFromDeclaration(position, matchingNode);
                return (symbol, selectedIndex);
            }

            if (matchingNode.IsKind(SyntaxKind.ObjectCreationExpression))
            {
                var objectCreation = matchingNode as ObjectCreationExpressionSyntax;

                if (token.Parent.AncestorsAndSelf().Any(a => a == objectCreation.Type))
                {
                    var typeSymbol = semanticModel.GetSymbolInfo(objectCreation.Type, cancellationToken).Symbol;
                    if (typeSymbol != null && typeSymbol.IsKind(SymbolKind.NamedType) && (typeSymbol as ITypeSymbol).TypeKind == TypeKind.Delegate)
                    {
                        return (typeSymbol, 0);
                    }
                }
            }

            var symbolInfo = semanticModel.GetSymbolInfo(matchingNode, cancellationToken);
            return (symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault(), 0);
        }

        private static int TryGetSelectedIndexFromDeclaration(int position, SyntaxNode matchingNode)
        {
            var parameters = matchingNode.ChildNodes().OfType<BaseParameterListSyntax>().SingleOrDefault();
            return parameters != null ? GetParameterIndex(parameters.Parameters, position) : 0;
        }

        private SyntaxNode GetMatchingNode(SyntaxNode node, bool restrictToDeclarations)
        {
            var matchKinds = restrictToDeclarations
                ? _declarationKinds
                : _declarationAndInvocableKinds;

            for (var current = node; current != null; current = current.Parent)
            {
                if (restrictToDeclarations &&
                    current.Kind() == SyntaxKind.Block || current.Kind() == SyntaxKind.ArrowExpressionClause)
                {
                    return null;
                }

                if (matchKinds.Contains(current.Kind()))
                {
                    return current;
                }
            }

            return null;
        }

        private bool InSymbolHeader(SyntaxNode matchingNode, int position)
        {
            // Caret has to be after the attributes if the symbol has any.
            var lastAttributes = matchingNode.ChildNodes().LastOrDefault(n => n is AttributeListSyntax);
            var start = lastAttributes?.GetLastToken().GetNextToken().SpanStart ??
                        matchingNode.SpanStart;

            if (position < start)
            {
                return false;
            }

            // If the symbol has a parameter list, then the caret shouldn't be past the end of it.
            var parameterList = matchingNode.ChildNodes().LastOrDefault(n => n is ParameterListSyntax);
            if (parameterList != null)
            {
                return position <= parameterList.FullSpan.End;
            }

            // Case we haven't handled yet.  Just assume we're in the header.
            return true;
        }

        public override SyntaxNode FindNodeToUpdate(Document document, SyntaxNode node)
        {
            if (_updatableNodeKinds.Contains(node.Kind()))
            {
                return node;
            }

            // TODO: file bug about this: var invocation = csnode.Ancestors().FirstOrDefault(a => a.Kind == SyntaxKind.InvocationExpression);
            var matchingNode = node.AncestorsAndSelf().FirstOrDefault(n => _updatableAncestorKinds.Contains(n.Kind()));
            if (matchingNode == null)
            {
                return null;
            }

            var nodeContainingOriginal = GetNodeContainingTargetNode(matchingNode);
            if (nodeContainingOriginal == null)
            {
                return null;
            }

            return node.AncestorsAndSelf().Any(n => n == nodeContainingOriginal) ? matchingNode : null;
        }

        private SyntaxNode GetNodeContainingTargetNode(SyntaxNode matchingNode)
        {
            switch (matchingNode.Kind())
            {
                case SyntaxKind.InvocationExpression:
                    return (matchingNode as InvocationExpressionSyntax).Expression;

                case SyntaxKind.ElementAccessExpression:
                    return (matchingNode as ElementAccessExpressionSyntax).ArgumentList;

                case SyntaxKind.ObjectCreationExpression:
                    return (matchingNode as ObjectCreationExpressionSyntax).Type;

                case SyntaxKind.ConstructorDeclaration:
                case SyntaxKind.IndexerDeclaration:
                case SyntaxKind.ThisConstructorInitializer:
                case SyntaxKind.BaseConstructorInitializer:
                case SyntaxKind.Attribute:
                case SyntaxKind.DelegateDeclaration:
                case SyntaxKind.NameMemberCref:
                    return matchingNode;

                default:
                    return null;
            }
        }

        public override SyntaxNode ChangeSignature(
            Document document,
            ISymbol declarationSymbol,
            SyntaxNode potentiallyUpdatedNode,
            SyntaxNode originalNode,
            SignatureChange signaturePermutation,
            CancellationToken cancellationToken)
        {
            var updatedNode = potentiallyUpdatedNode as CSharpSyntaxNode;

            // Update <param> tags.

            if (updatedNode.IsKind(SyntaxKind.MethodDeclaration) ||
                updatedNode.IsKind(SyntaxKind.ConstructorDeclaration) ||
                updatedNode.IsKind(SyntaxKind.IndexerDeclaration) ||
                updatedNode.IsKind(SyntaxKind.DelegateDeclaration))
            {
                var updatedLeadingTrivia = UpdateParamTagsInLeadingTrivia(updatedNode, declarationSymbol, signaturePermutation);
                if (updatedLeadingTrivia != null)
                {
                    updatedNode = updatedNode.WithLeadingTrivia(updatedLeadingTrivia);
                }
            }

            // Update declarations parameter lists

            if (updatedNode.IsKind(SyntaxKind.MethodDeclaration, out MethodDeclarationSyntax method))
            {
                var updatedParameters = PermuteDeclaration(method.ParameterList.Parameters, signaturePermutation);
                return method.WithParameterList(method.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.LocalFunctionStatement, out LocalFunctionStatementSyntax localFunction))
            {
                var updatedParameters = PermuteDeclaration(localFunction.ParameterList.Parameters, signaturePermutation);
                return localFunction.WithParameterList(localFunction.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ConstructorDeclaration, out ConstructorDeclarationSyntax constructor))
            {
                var updatedParameters = PermuteDeclaration(constructor.ParameterList.Parameters, signaturePermutation);
                return constructor.WithParameterList(constructor.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.IndexerDeclaration, out IndexerDeclarationSyntax indexer))
            {
                var updatedParameters = PermuteDeclaration(indexer.ParameterList.Parameters, signaturePermutation);
                return indexer.WithParameterList(indexer.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.DelegateDeclaration, out DelegateDeclarationSyntax delegateDeclaration))
            {
                var updatedParameters = PermuteDeclaration(delegateDeclaration.ParameterList.Parameters, signaturePermutation);
                return delegateDeclaration.WithParameterList(delegateDeclaration.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.AnonymousMethodExpression, out AnonymousMethodExpressionSyntax anonymousMethod))
            {
                // Delegates may omit parameters in C#
                if (anonymousMethod.ParameterList == null)
                {
                    return anonymousMethod;
                }

                var updatedParameters = PermuteDeclaration(anonymousMethod.ParameterList.Parameters, signaturePermutation);
                return anonymousMethod.WithParameterList(anonymousMethod.ParameterList.WithParameters(updatedParameters).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.SimpleLambdaExpression, out SimpleLambdaExpressionSyntax lambda))
            {
                if (signaturePermutation.UpdatedConfiguration.ToListOfParameters().Any())
                {
                    Debug.Assert(false, "Updating a simple lambda expression without removing its parameter");
                }
                else
                {
                    // No parameters. Change to a parenthesized lambda expression

                    var emptyParameterList = SyntaxFactory.ParameterList()
                        .WithLeadingTrivia(lambda.Parameter.GetLeadingTrivia())
                        .WithTrailingTrivia(lambda.Parameter.GetTrailingTrivia());

                    return SyntaxFactory.ParenthesizedLambdaExpression(lambda.AsyncKeyword, emptyParameterList, lambda.ArrowToken, lambda.Body);
                }
            }

            if (updatedNode.IsKind(SyntaxKind.ParenthesizedLambdaExpression, out ParenthesizedLambdaExpressionSyntax parenLambda))
            {
                var updatedParameters = PermuteDeclaration(parenLambda.ParameterList.Parameters, signaturePermutation);
                return parenLambda.WithParameterList(parenLambda.ParameterList.WithParameters(updatedParameters));
            }

            // Update reference site argument lists

            if (updatedNode.IsKind(SyntaxKind.InvocationExpression, out InvocationExpressionSyntax invocation))
            {
                var semanticModel = document.GetSemanticModelAsync(cancellationToken).WaitAndGetResult_CanCallOnBackground(cancellationToken);

                var symbolInfo = semanticModel.GetSymbolInfo((InvocationExpressionSyntax)originalNode, cancellationToken);
                var isReducedExtensionMethod = false;

                if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.MethodKind == MethodKind.ReducedExtension)
                {
                    isReducedExtensionMethod = true;
                }

                var newArguments = PermuteArgumentList(declarationSymbol, invocation.ArgumentList.Arguments, signaturePermutation, isReducedExtensionMethod);
                return invocation.WithArgumentList(invocation.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ObjectCreationExpression, out ObjectCreationExpressionSyntax objCreation))
            {
                var newArguments = PermuteArgumentList(declarationSymbol, objCreation.ArgumentList.Arguments, signaturePermutation);
                return objCreation.WithArgumentList(objCreation.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ThisConstructorInitializer) ||
                updatedNode.IsKind(SyntaxKind.BaseConstructorInitializer))
            {
                var constructorInit = (ConstructorInitializerSyntax)updatedNode;
                var newArguments = PermuteArgumentList(declarationSymbol, constructorInit.ArgumentList.Arguments, signaturePermutation);
                return constructorInit.WithArgumentList(constructorInit.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ElementAccessExpression, out ElementAccessExpressionSyntax elementAccess))
            {
                var newArguments = PermuteArgumentList(declarationSymbol, elementAccess.ArgumentList.Arguments, signaturePermutation);
                return elementAccess.WithArgumentList(elementAccess.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.Attribute, out AttributeSyntax attribute))
            {
                var newArguments = PermuteAttributeArgumentList(declarationSymbol, attribute.ArgumentList.Arguments, signaturePermutation);
                return attribute.WithArgumentList(attribute.ArgumentList.WithArguments(newArguments).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            // Handle references in crefs

            if (updatedNode.IsKind(SyntaxKind.NameMemberCref, out NameMemberCrefSyntax nameMemberCref))
            {
                if (nameMemberCref.Parameters == null ||
                    !nameMemberCref.Parameters.Parameters.Any())
                {
                    return nameMemberCref;
                }

                var newParameters = PermuteDeclaration(nameMemberCref.Parameters.Parameters, signaturePermutation);

                var newCrefParameterList = nameMemberCref.Parameters.WithParameters(newParameters);
                return nameMemberCref.WithParameters(newCrefParameterList);
            }

            Debug.Assert(false, "Unknown reference location");
            return null;
        }

        private SeparatedSyntaxList<T> PermuteDeclaration<T>(SeparatedSyntaxList<T> list, SignatureChange updatedSignature) where T : SyntaxNode
        {
            var originalParameters = updatedSignature.OriginalConfiguration.ToListOfParameters();
            var reorderedParameters = updatedSignature.UpdatedConfiguration.ToListOfParameters();

            var newParameters = new List<T>();
            var newSeparators = new SyntaxToken[list.Count];
            for (var newIndex = 0; newIndex < reorderedParameters.Count; newIndex++)
            {
                var newParamSymbol = reorderedParameters[newIndex];
                var originalIndex = originalParameters.IndexOf(newParamSymbol);

                // Transferring over trivia both from the node any associated separator.
                var (newParamNode, newSeparator) = TransferTrivia(list, originalIndex, newIndex);

                newParameters.Add(newParamNode);
                newSeparators[newIndex] = newSeparator;
            }

            return SyntaxFactory.SeparatedList(newParameters, newSeparators.ToList().GetRange(0, reorderedParameters.Count == 0 ? 0 : reorderedParameters.Count - 1));
        }

        private static (T newParamNode, SyntaxToken newSeparators) TransferTrivia<T>(SeparatedSyntaxList<T> list, int originalIndex, int newIndex) where T : SyntaxNode
        {
            var newParamNode = list[originalIndex];

            // Copy whitespace trivia from original position.
            newParamNode = TransferLeadingWhitespaceTrivia(newParamNode, list[newIndex]);

            // The separator associated with the node should have its trivia transferred over as well. If we're looking at the last node, then there is no associated separator.
            var originalSeparator = originalIndex == list.Count - 1 ? SyntaxFactory.Token(SyntaxKind.None) : list.GetSeparator(originalIndex);
            var newSeparator = newIndex == list.Count - 1 ? SyntaxFactory.Token(SyntaxKind.None) : list.GetSeparator(newIndex);

            // If we have a comment of format '//' and the associated node is switching positions, we switch the trivia notation to /* */.
            if (list[newIndex] != list[originalIndex])
            {
                (newParamNode, originalSeparator) = ChangeSingleLineCommentToMultiLine(newParamNode, originalSeparator);
            }

            // Case 1: The current node in the updated configuration is the last parameter.
            // Append any trailing trivia from the original separator to the trailing trivia of the new node, as long as it's not all whitespace. We also want to ignore end-of-line trivia.
            if (newSeparator.IsKind(SyntaxKind.None))
            {
                if (originalSeparator.TrailingTrivia.Any(trivia => !trivia.IsKind(SyntaxKind.WhitespaceTrivia)))
                {
                    newParamNode = newParamNode.WithAppendedTrailingTrivia(originalSeparator.TrailingTrivia.Where(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
                }
            }
            // Case 2: The current node in the updated configuration is not the last parameter.
            else
            {
                // We want to transfer over the comments from the original separator to the new separator. However, we also want to preserve the new
                // signature's formatting. To accomplish this, if the original separator has any comments, we will append them at the end of the new separator's
                // trailing trivia, but before any end-of-line trivia the new separator may have. Existing comments in the new separator will be removed, as they
                // will be placed in the correct locations in previous or future iterations of the loop.
                var originalSeparatorComments = originalSeparator.TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia));

                // If there are multiple comments, preserve the trivia between them.
                if (originalSeparatorComments.Count() > 1)
                {
                    var originalSeparatorCommentsWithTrivia = originalSeparator.TrailingTrivia.TakeWhile(trivia => !trivia.Equals(originalSeparatorComments.Last()));
                    originalSeparatorCommentsWithTrivia = originalSeparatorCommentsWithTrivia.Concat(originalSeparatorComments.Last());

                    originalSeparatorComments = originalSeparatorCommentsWithTrivia;
                }

                // Remove end-of-line trivia and comments from the new separator, and add any comments from the original separator.
                var updatedTrivia = newSeparator.TrailingTrivia.Where(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                                                                      && !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).Concat(originalSeparatorComments);

                // Add end-of-line trivia back, if there were any to begin with.
                updatedTrivia = updatedTrivia.Concat(newSeparator.TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
                newSeparator = newSeparator.WithTrailingTrivia(updatedTrivia);
            }

            return (newParamNode, newSeparator);
        }

        private static T TransferLeadingWhitespaceTrivia<T>(T newArgument, SyntaxNode oldArgument) where T : SyntaxNode
        {
            var oldTrivia = oldArgument.GetLeadingTrivia();
            var newTrivia = newArgument.GetLeadingTrivia();

            var oldLeadingWhitespaceTrivia = oldTrivia.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            var newLeadingWhitespaceTrivia = newTrivia.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
            var newLeadingNonWhitespaceTrivia = newTrivia.TakeRange(newLeadingWhitespaceTrivia.Count(), newTrivia.Count - 1);

            return newArgument.WithLeadingTrivia(oldLeadingWhitespaceTrivia.Concat(newLeadingNonWhitespaceTrivia));
        }

        private static (T, SyntaxToken) ChangeSingleLineCommentToMultiLine<T>(T param, SyntaxToken separator) where T : SyntaxNode
        {
            var paramTrailingTrivia = param.GetTrailingTrivia();
            var separatorTrailingTrivia = separator.TrailingTrivia;

            var paramSingleLineCommentTrivia = paramTrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).FirstOrDefault();
            if (paramSingleLineCommentTrivia != default)
            {
                param = param.ReplaceTrivia(paramSingleLineCommentTrivia, GenerateMultiLineComment(paramSingleLineCommentTrivia));
            }

            var separatorSingleLineCommentTrivia = separatorTrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).FirstOrDefault();
            if (separatorSingleLineCommentTrivia != default)
            {
                separator = separator.ReplaceTrivia(separatorSingleLineCommentTrivia, GenerateMultiLineComment(separatorSingleLineCommentTrivia));
            }

            return (param, separator);

            static SyntaxTrivia GenerateMultiLineComment(SyntaxTrivia singleLineComment)
            {
                var commentText = singleLineComment.GetCommentText();
                return SyntaxFactory.Comment("/* " + commentText + " */");
            }
        }

        private static SeparatedSyntaxList<AttributeArgumentSyntax> PermuteAttributeArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<AttributeArgumentSyntax> arguments,
            SignatureChange updatedSignature)
        {
            var originalArguments = arguments.Select(a => UnifiedArgumentSyntax.Create(a, arguments.IndexOf(a))).ToList();
            var permutedArguments = PermuteArguments(declarationSymbol, originalArguments, updatedSignature);

            var finalArguments = new List<AttributeArgumentSyntax>();
            var newSeparators = new SyntaxToken[originalArguments.Count];
            for (var newIndex = 0; newIndex < permutedArguments.Count; newIndex++)
            {
                var argument = permutedArguments[newIndex];
                var originalIndex = ((UnifiedArgumentSyntax)argument).Index;

                var (newParamNode, newSeparator) = TransferTrivia(arguments, originalIndex, newIndex);

                finalArguments.Add(newParamNode);
                newSeparators[newIndex] = newSeparator;
            }

            return SyntaxFactory.SeparatedList(finalArguments, newSeparators.ToList().GetRange(0, permutedArguments.Count == 0 ? 0 : permutedArguments.Count - 1));
        }

        private static SeparatedSyntaxList<ArgumentSyntax> PermuteArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SignatureChange updatedSignature,
            bool isReducedExtensionMethod = false)
        {
            var originalArguments = arguments.Select(a => UnifiedArgumentSyntax.Create(a, arguments.IndexOf(a))).ToList();
            var permutedArguments = PermuteArguments(declarationSymbol, originalArguments, updatedSignature, isReducedExtensionMethod);

            var finalArguments = new List<ArgumentSyntax>();
            var newSeparators = new SyntaxToken[originalArguments.Count];
            for (var newIndex = 0; newIndex < permutedArguments.Count; newIndex++)
            {
                var argument = permutedArguments[newIndex];
                var originalIndex = ((UnifiedArgumentSyntax)argument).Index;

                var (newParamNode, newSeparator) = TransferTrivia(arguments, originalIndex, newIndex);

                finalArguments.Add(newParamNode);
                newSeparators[newIndex] = newSeparator;
            }

            return SyntaxFactory.SeparatedList(finalArguments, newSeparators.ToList().GetRange(0, permutedArguments.Count == 0 ? 0 : permutedArguments.Count - 1));
        }

        private List<SyntaxTrivia> UpdateParamTagsInLeadingTrivia(CSharpSyntaxNode node, ISymbol declarationSymbol, SignatureChange updatedSignature)
        {
            if (!node.HasLeadingTrivia)
            {
                return null;
            }

            var paramNodes = node
                .DescendantNodes(descendIntoTrivia: true)
                .OfType<XmlElementSyntax>()
                .Where(e => e.StartTag.Name.ToString() == DocumentationCommentXmlNames.ParameterElementName);

            var permutedParamNodes = VerifyAndPermuteParamNodes(paramNodes, declarationSymbol, updatedSignature);
            if (permutedParamNodes == null)
            {
                return null;
            }

            return GetPermutedTrivia(node, permutedParamNodes);
        }

        private List<XmlElementSyntax> VerifyAndPermuteParamNodes(IEnumerable<XmlElementSyntax> paramNodes, ISymbol declarationSymbol, SignatureChange updatedSignature)
        {
            // Only reorder if count and order match originally.

            var originalParameters = updatedSignature.OriginalConfiguration.ToListOfParameters();
            var reorderedParameters = updatedSignature.UpdatedConfiguration.ToListOfParameters();

            var declaredParameters = declarationSymbol.GetParameters();
            if (paramNodes.Count() != declaredParameters.Length)
            {
                return null;
            }

            var dictionary = new Dictionary<string, XmlElementSyntax>();
            var i = 0;
            foreach (var paramNode in paramNodes)
            {
                var nameAttribute = paramNode.StartTag.Attributes.FirstOrDefault(a => a.Name.ToString().Equals("name", StringComparison.OrdinalIgnoreCase));
                if (nameAttribute == null)
                {
                    return null;
                }

                var identifier = nameAttribute.DescendantNodes(descendIntoTrivia: true).OfType<IdentifierNameSyntax>().FirstOrDefault();
                if (identifier == null || identifier.ToString() != declaredParameters.ElementAt(i).Name)
                {
                    return null;
                }

                dictionary.Add(originalParameters[i].Name.ToString(), paramNode);
                i++;
            }

            // Everything lines up, so permute them.

            var permutedParams = new List<XmlElementSyntax>();
            foreach (var parameter in reorderedParameters)
            {
                permutedParams.Add(dictionary[parameter.Name]);
            }

            return permutedParams;
        }

        private List<SyntaxTrivia> GetPermutedTrivia(CSharpSyntaxNode node, List<XmlElementSyntax> permutedParamNodes)
        {
            var updatedLeadingTrivia = new List<SyntaxTrivia>();
            var index = 0;

            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (!trivia.HasStructure)
                {
                    updatedLeadingTrivia.Add(trivia);
                    continue;
                }

                if (!(trivia.GetStructure() is DocumentationCommentTriviaSyntax structuredTrivia))
                {
                    updatedLeadingTrivia.Add(trivia);
                    continue;
                }

                var updatedNodeList = new List<XmlNodeSyntax>();
                var structuredContent = structuredTrivia.Content.ToList();
                for (var i = 0; i < structuredContent.Count; i++)
                {
                    var content = structuredContent[i];
                    if (!content.IsKind(SyntaxKind.XmlElement))
                    {
                        updatedNodeList.Add(content);
                        continue;
                    }

                    var xmlElement = content as XmlElementSyntax;
                    if (xmlElement.StartTag.Name.ToString() != DocumentationCommentXmlNames.ParameterElementName)
                    {
                        updatedNodeList.Add(content);
                        continue;
                    }

                    // Found a param tag, so insert the next one from the reordered list
                    if (index < permutedParamNodes.Count)
                    {
                        updatedNodeList.Add(permutedParamNodes[index].WithLeadingTrivia(content.GetLeadingTrivia()).WithTrailingTrivia(content.GetTrailingTrivia()));
                        index++;
                    }
                    else
                    {
                        // Inspecting a param element that we are deleting but not replacing.
                    }
                }

                var newDocComments = SyntaxFactory.DocumentationCommentTrivia(structuredTrivia.Kind(), SyntaxFactory.List(updatedNodeList.AsEnumerable()));
                newDocComments = newDocComments.WithEndOfComment(structuredTrivia.EndOfComment);
                newDocComments = newDocComments.WithLeadingTrivia(structuredTrivia.GetLeadingTrivia()).WithTrailingTrivia(structuredTrivia.GetTrailingTrivia());
                var newTrivia = SyntaxFactory.Trivia(newDocComments);

                updatedLeadingTrivia.Add(newTrivia);
            }

            return updatedLeadingTrivia;
        }

        public override async Task<ImmutableArray<SymbolAndProjectId>> DetermineCascadedSymbolsFromDelegateInvoke(
            SymbolAndProjectId<IMethodSymbol> symbolAndProjectId,
            Document document,
            CancellationToken cancellationToken)
        {
            var symbol = symbolAndProjectId.Symbol;
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var nodes = root.DescendantNodes().ToImmutableArray();
            var convertedMethodGroups = nodes
                .WhereAsArray(
                    n =>
                        {
                            if (!n.IsKind(SyntaxKind.IdentifierName) ||
                                !semanticModel.GetMemberGroup(n, cancellationToken).Any())
                            {
                                return false;
                            }

                            ISymbol convertedType = semanticModel.GetTypeInfo(n, cancellationToken).ConvertedType;

                            if (convertedType != null)
                            {
                                convertedType = convertedType.OriginalDefinition;
                            }

                            if (convertedType != null)
                            {
                                convertedType = SymbolFinder.FindSourceDefinitionAsync(convertedType, document.Project.Solution, cancellationToken).WaitAndGetResult_CanCallOnBackground(cancellationToken) ?? convertedType;
                            }

                            return Equals(convertedType, symbol.ContainingType);
                        })
                .SelectAsArray(n => semanticModel.GetSymbolInfo(n, cancellationToken).Symbol);

            return convertedMethodGroups.SelectAsArray(symbolAndProjectId.WithSymbol);
        }

        protected override IEnumerable<AbstractFormattingRule> GetFormattingRules(Document document)
        {
            return SpecializedCollections.SingletonEnumerable(new ChangeSignatureFormattingRule()).Concat(Formatter.GetDefaultFormattingRules(document));
        }
    }
}
