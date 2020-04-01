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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ChangeSignature;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Structure;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;
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

            if (matchingNode.IsKind(SyntaxKind.ObjectCreationExpression, out ObjectCreationExpressionSyntax objectCreation) &&
                token.Parent.AncestorsAndSelf().Any(a => a == objectCreation.Type))
            {
                var typeSymbol = semanticModel.GetSymbolInfo(objectCreation.Type, cancellationToken).Symbol;
                if (typeSymbol != null && typeSymbol.IsKind(SymbolKind.NamedType) && (typeSymbol as ITypeSymbol).TypeKind == TypeKind.Delegate)
                {
                    return (typeSymbol, 0);
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

            // Update declarations parameter lists.
            // In order to propelry preserve comments, we must move the trailing trivia on the open paren token to the leading trivia of the original
            // first node, such as in the case below:
            //     M(/* comment */ a, b)
            // In the above case, by default, the comment is associated with the open paren token, while we want it to be associated with node 'a'.

            if (updatedNode.IsKind(SyntaxKind.MethodDeclaration, out MethodDeclarationSyntax method))
            {
                var parameterList = method.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return method.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.LocalFunctionStatement, out LocalFunctionStatementSyntax localFunction))
            {
                var parameterList = localFunction.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return localFunction.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ConstructorDeclaration, out ConstructorDeclarationSyntax constructor))
            {
                var parameterList = constructor.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return constructor.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.IndexerDeclaration, out IndexerDeclarationSyntax indexer))
            {
                var parameterList = indexer.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenBracketToken, signaturePermutation);
                return indexer.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenBracketToken(parameterList.OpenBracketToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.DelegateDeclaration, out DelegateDeclarationSyntax delegateDeclaration))
            {
                var parameterList = delegateDeclaration.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return delegateDeclaration.WithParameterList(
                    parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.AnonymousMethodExpression, out AnonymousMethodExpressionSyntax anonymousMethod))
            {
                // Delegates may omit parameters in C#
                if (anonymousMethod.ParameterList == null)
                {
                    return anonymousMethod;
                }

                var parameterList = anonymousMethod.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return anonymousMethod.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia().WithAdditionalAnnotations(changeSignatureFormattingAnnotation)));
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
                var parameterList = parenLambda.ParameterList;
                var updatedParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);
                return parenLambda.WithParameterList(parameterList.WithParameters(updatedParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia()));
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

                var argumentList = invocation.ArgumentList;
                var newArguments = PermuteArgumentList(declarationSymbol, argumentList.Arguments, argumentList.OpenParenToken, signaturePermutation, isReducedExtensionMethod);
                return invocation.WithArgumentList(argumentList.WithArguments(newArguments).WithOpenParenToken(argumentList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ObjectCreationExpression, out ObjectCreationExpressionSyntax objCreation))
            {
                var argumentList = objCreation.ArgumentList;
                var newArguments = PermuteArgumentList(declarationSymbol, argumentList.Arguments, argumentList.OpenParenToken, signaturePermutation);
                return objCreation.WithArgumentList(argumentList.WithArguments(newArguments).WithOpenParenToken(argumentList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ThisConstructorInitializer, out ConstructorInitializerSyntax constructorInit) ||
                updatedNode.IsKind(SyntaxKind.BaseConstructorInitializer, out constructorInit))
            {
                var argumentList = constructorInit.ArgumentList;
                var newArguments = PermuteArgumentList(declarationSymbol, argumentList.Arguments, argumentList.OpenParenToken, signaturePermutation);
                return constructorInit.WithArgumentList(argumentList.WithArguments(newArguments).WithOpenParenToken(argumentList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.ElementAccessExpression, out ElementAccessExpressionSyntax elementAccess))
            {
                var argumentList = elementAccess.ArgumentList;
                var newArguments = PermuteArgumentList(declarationSymbol, argumentList.Arguments, argumentList.OpenBracketToken, signaturePermutation);
                return elementAccess.WithArgumentList(argumentList.WithArguments(newArguments).WithOpenBracketToken(argumentList.OpenBracketToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            if (updatedNode.IsKind(SyntaxKind.Attribute, out AttributeSyntax attribute))
            {
                var argumentList = attribute.ArgumentList;
                var newArguments = PermuteAttributeArgumentList(declarationSymbol, argumentList.Arguments, argumentList.OpenParenToken, signaturePermutation);
                return attribute.WithArgumentList(argumentList.WithArguments(newArguments).WithOpenParenToken(argumentList.OpenParenToken.WithoutTrailingTrivia()).WithAdditionalAnnotations(changeSignatureFormattingAnnotation));
            }

            // Handle references in crefs

            if (updatedNode.IsKind(SyntaxKind.NameMemberCref, out NameMemberCrefSyntax nameMemberCref))
            {
                if (nameMemberCref.Parameters == null ||
                    !nameMemberCref.Parameters.Parameters.Any())
                {
                    return nameMemberCref;
                }

                var parameterList = nameMemberCref.Parameters;
                var newParameters = PermuteDeclaration(parameterList.Parameters, parameterList.OpenParenToken, signaturePermutation);

                var newCrefParameterList = parameterList.WithParameters(newParameters).WithOpenParenToken(parameterList.OpenParenToken.WithoutTrailingTrivia());
                return nameMemberCref.WithParameters(newCrefParameterList);
            }

            Debug.Assert(false, "Unknown reference location");
            return null;
        }

        private SeparatedSyntaxList<T> PermuteDeclaration<T>(
            SeparatedSyntaxList<T> list,
            SyntaxToken openParenToken,
            SignatureChange updatedSignature) where T : SyntaxNode
        {
            var originalParameters = updatedSignature.OriginalConfiguration.ToListOfParameters();
            var reorderedParameters = updatedSignature.UpdatedConfiguration.ToListOfParameters();

            var newParameters = new List<T>();

            // If we have no parameters, we don't need to do any work.
            if (reorderedParameters.Count == 0)
            {
                return SyntaxFactory.SeparatedList(newParameters, Array.Empty<SyntaxToken>());
            }

            var newSeparatorsBuilder = ArrayBuilder<SyntaxToken>.GetInstance(reorderedParameters.Count - 1);

            // We may want the trailing trivia of a separator to apply to different nodes in different cases. Before doing the bulk of the work, apply minor adjustments to trivia.
            var adjustedList = AdjustSeparatorTrivia(list);
            for (var newIndex = 0; newIndex < reorderedParameters.Count; newIndex++)
            {
                var newParamSymbol = reorderedParameters[newIndex];
                var originalIndex = originalParameters.IndexOf(newParamSymbol);

                // Transferring over trivia both from the node and associated separator.
                var (newParamNode, newSeparator) = TransferTrivia(adjustedList, adjustedList[originalIndex], originalIndex, newIndex, reorderedParameters.Count);

                // We also need to transfer over the trailing trivia from the open paren token since there may be comments/trivia there.
                // Ex.: M(/* comment */ a, b) -> by default, the comment is associated with the open paren, while we instead want it associated with node 'a'.
                newParamNode = TransferTriviaFromOpenParen<T>(openParenToken, newIndex, originalIndex, newParamNode);

                newParameters.Add(newParamNode);

                // The last node does't have a separator.
                if (newIndex != reorderedParameters.Count - 1)
                {
                    newSeparatorsBuilder.SetItem(newIndex, newSeparator);
                }
            }

            return SyntaxFactory.SeparatedList(newParameters, newSeparatorsBuilder.ToSyntaxTokenListAndFree());
        }

        private static SeparatedSyntaxList<T> AdjustSeparatorTrivia<T>(SeparatedSyntaxList<T> list) where T : SyntaxNode
        {
            // This method is required since we may want the trailing trivia of a separator to apply to different nodes in different cases.
            // For example, in this case, we want the comment to be associated with node 'a':
            //     M(a, /* a */
            //       b);
            // 
            // However, in this case, we want the comment to be associated with node 'b':
            //     M(a, /* b */ b);

            var newParameters = list.ToList();
            var newSeparatorsBuilder = ArrayBuilder<SyntaxToken>.GetInstance(list.SeparatorCount);
            for (var separatorIndex = 0; separatorIndex < list.SeparatorCount; separatorIndex++)
            {
                var separator = list.GetSeparator(separatorIndex);

                // If the separator's trailing trivia does not contain any end-of-line trivia, we append the trailing trivia to the leading trivia of the node following.
                // Otherwise, we leave the separator as is.
                if (!separator.TrailingTrivia.Any(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)))
                {
                    newParameters[separatorIndex + 1] = newParameters[separatorIndex + 1].WithPrependedLeadingTrivia(separator.TrailingTrivia);
                    separator = separator.WithoutTrailingTrivia();
                }

                newSeparatorsBuilder.SetItem(separatorIndex, separator);
            }

            return SyntaxFactory.SeparatedList(newParameters, newSeparatorsBuilder.ToSyntaxTokenListAndFree());
        }

        private static (T newParamNode, SyntaxToken newSeparator) TransferTrivia<T>(
            SeparatedSyntaxList<T> list,
            T newParamNode,
            int originalIndex,
            int newIndex,
            int reorderedParametersCount) where T : SyntaxNode
        {
            // Copy leading whitespace trivia from the original position to new position.
            newParamNode = TransferLeadingWhitespaceTrivia(newParamNode, list[newIndex]);

            // The separator associated with the node should have its trivia transferred over as well. If we're looking at the last node, then there is no associated separator.
            var originalSeparator = originalIndex < list.Count - 1 ? list.GetSeparator(originalIndex) : default;
            var newSeparator = newIndex < reorderedParametersCount - 1 ? list.GetSeparator(newIndex) : default;

            // If the associated node is not switching positions, we don't need to do any more work.
            if (originalSeparator == newSeparator)
            {
                return (newParamNode, newSeparator);
            }

            // If we have a comment of format '//', we switch the trivia notation to /* */, since there's a possibility that we may be moving the parameter before
            // another parameter on the same line.
            // Ex.:
            //     M(a, b,
            //       c, // comment
            //       d)
            //
            // Switching parameters 'a' and 'c':
            //     M(c, /* comment */ b,
            //       a,
            //       d)
            newParamNode = UpdateTrailingSingleLineCommentsToMultiLine(newParamNode);
            originalSeparator = UpdateSeparatorSingleLineCommentsToMultiLine(originalSeparator);

            // Case 1: The current node in the updated configuration is the last parameter.
            // Since there's no new separator to transfer the trivia to, we instead append any trailing trivia from the original separator to the trailing trivia of the new node.
            // We ignore end-of-line trivia as they should be preserved in their original locations.
            // Ex.:
            //     M(a, b, // comment
            //       c)
            //
            // Switching parameters 'b' and 'c':
            //     M(a, c,
            //       b /* comment */)
            if (newSeparator == default)
            {
                newParamNode = newParamNode.WithAppendedTrailingTrivia(originalSeparator.TrailingTrivia.Where(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia)));
            }
            // Case 2: The current node in the updated configuration is not the last parameter.
            // If the original separator has any trailing comments or whitespace, we will append them at the end of the new separator's trailing trivia, but before any end-of-line trivia the
            // new separator may have. Existing comments in the new separator will be removed, as they have will be placed in their correct locations in previous/future iterations of the loop.
            // Ex.:
            //     M(a, b, // comment
            //       c, d)
            //
            // Switching parameters 'b' and 'c'. The original separator following 'b' has end-of-line trailing trivia that we want to preserve, but we want to move its comment and whitespace
            // to the new separator of 'b':
            // M(a, c,
            //   b, /* comment */ d)
            else
            {
                // Get the comments and whitespace of the original separator.
                var originalSeparatorCommentsAndWhitespace = originalSeparator.TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) || trivia.IsKind(SyntaxKind.WhitespaceTrivia));

                // Remove trailing end-of-line trivia (temporarily) and comments from the new separator, and add any comments and whitespace from the original separator.
                var updatedTrivia = newSeparator.TrailingTrivia.Where(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                                                                    && !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.WhitespaceTrivia)).Concat(originalSeparatorCommentsAndWhitespace);

                // Add end-of-line trivia back, if there were any to begin with.
                updatedTrivia = updatedTrivia.Concat(newSeparator.TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)));

                // Override the new separator's trailing trivia with updated trivia.
                newSeparator = newSeparator.WithTrailingTrivia(updatedTrivia);
            }

            return (newParamNode, newSeparator);

            static T TransferLeadingWhitespaceTrivia(T newArgument, SyntaxNode oldArgument)
            {
                var oldTrivia = oldArgument.GetLeadingTrivia();
                var newTrivia = newArgument.GetLeadingTrivia();

                var oldLeadingWhitespaceTrivia = oldTrivia.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
                var newLeadingWhitespaceTrivia = newTrivia.TakeWhile(t => t.IsKind(SyntaxKind.WhitespaceTrivia));
                var newLeadingNonWhitespaceTrivia = newTrivia.TakeRange(newLeadingWhitespaceTrivia.Count(), newTrivia.Count - 1);

                return newArgument.WithLeadingTrivia(oldLeadingWhitespaceTrivia.Concat(newLeadingNonWhitespaceTrivia));
            }

            static T UpdateTrailingSingleLineCommentsToMultiLine(T param)
            {
                var paramTrailingTrivia = param.GetTrailingTrivia();

                var paramSingleLineCommentTrivia = paramTrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).FirstOrDefault();
                if (paramSingleLineCommentTrivia != default)
                {
                    param = param.ReplaceTrivia(paramSingleLineCommentTrivia, GenerateMultiLineComment(paramSingleLineCommentTrivia));
                }

                return param;
            }

            static SyntaxToken UpdateSeparatorSingleLineCommentsToMultiLine(SyntaxToken separator)
            {
                var separatorLeadingTrivia = separator.LeadingTrivia;
                var separatorTrailingTrivia = separator.TrailingTrivia;

                var separatorSingleLineCommentLeadingTrivia = separatorLeadingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).FirstOrDefault();
                if (separatorSingleLineCommentLeadingTrivia != default)
                {
                    separator = separator.ReplaceTrivia(separatorSingleLineCommentLeadingTrivia, GenerateMultiLineComment(separatorSingleLineCommentLeadingTrivia));
                }

                var separatorSingleLineCommentTrailingTrivia = separatorTrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)).FirstOrDefault();
                if (separatorSingleLineCommentTrailingTrivia != default)
                {
                    separator = separator.ReplaceTrivia(separatorSingleLineCommentTrailingTrivia, GenerateMultiLineComment(separatorSingleLineCommentTrailingTrivia));
                }

                return separator;
            }

            static SyntaxTrivia GenerateMultiLineComment(SyntaxTrivia singleLineComment)
            {
                var commentText = singleLineComment.GetCommentText();
                return SyntaxFactory.Comment("/* " + commentText + " */");
            }
        }

        private static T TransferTriviaFromOpenParen<T>(SyntaxToken openParenToken, int newIndex, int originalIndex, T newParamNode) where T : SyntaxNode
        {
            if (originalIndex == 0)
            {
                newParamNode = newParamNode.WithLeadingTrivia<T>(newParamNode.GetLeadingTrivia().Concat(openParenToken.TrailingTrivia.Where(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia))));
            }

            if (newIndex == 0)
            {
                newParamNode = newParamNode.WithLeadingTrivia<T>(openParenToken.TrailingTrivia.Where(trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia)).Concat(newParamNode.GetLeadingTrivia()));
            }

            return newParamNode;
        }

        private static SeparatedSyntaxList<AttributeArgumentSyntax> PermuteAttributeArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<AttributeArgumentSyntax> arguments,
            SyntaxToken openParenToken,
            SignatureChange updatedSignature)
        {
            // We may want the trailing trivia of a separator to apply to different nodes in different cases. Before doing the bulk of the work, apply minor adjustments to trivia.
            var adjustedArguments = AdjustSeparatorTrivia(arguments);
            var originalArguments = adjustedArguments.Select(a => UnifiedArgumentSyntax.Create(a, adjustedArguments.IndexOf(a))).ToList();
            var permutedArguments = PermuteArguments(declarationSymbol, originalArguments, updatedSignature);

            // If we have no arguments, we don't need to do any work.
            if (permutedArguments.Count == 0)
            {
                return SyntaxFactory.SeparatedList(new List<AttributeArgumentSyntax>(), new List<SyntaxToken>());
            }

            var newSeparatorsBuilder = ArrayBuilder<SyntaxToken>.GetInstance(permutedArguments.Count - 1);
            return GetFinalPermutedArgumentsWithTrivia(adjustedArguments, openParenToken, permutedArguments, newSeparatorsBuilder);
        }

        private static SeparatedSyntaxList<ArgumentSyntax> PermuteArgumentList(
            ISymbol declarationSymbol,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            SyntaxToken openParenToken,
            SignatureChange updatedSignature,
            bool isReducedExtensionMethod = false)
        {
            // We may want the trailing trivia of a separator to apply to different nodes in different cases. Before doing the bulk of the work, apply minor adjustments to trivia.
            var adjustedArguments = AdjustSeparatorTrivia(arguments);
            var unifiedArguments = adjustedArguments.Select(a => UnifiedArgumentSyntax.Create(a, adjustedArguments.IndexOf(a))).ToList();
            var permutedArguments = PermuteArguments(declarationSymbol, unifiedArguments, updatedSignature, isReducedExtensionMethod);

            // If we have no arguments, we don't need to do any work.
            if (permutedArguments.Count == 0)
            {
                return SyntaxFactory.SeparatedList(new List<ArgumentSyntax>(), new List<SyntaxToken>());
            }

            var newSeparatorsBuilder = ArrayBuilder<SyntaxToken>.GetInstance(permutedArguments.Count - 1);
            return GetFinalPermutedArgumentsWithTrivia(adjustedArguments, openParenToken, permutedArguments, newSeparatorsBuilder);
        }

        private static SeparatedSyntaxList<T> GetFinalPermutedArgumentsWithTrivia<T>(
            SeparatedSyntaxList<T> arguments,
            SyntaxToken openParenToken,
            List<IUnifiedArgumentSyntax> permutedArguments,
            ArrayBuilder<SyntaxToken> newSeparatorsBuilder) where T : SyntaxNode
        {
            var finalArguments = new List<T>();
            for (var newIndex = 0; newIndex < permutedArguments.Count; newIndex++)
            {
                var argument = permutedArguments[newIndex];
                var originalIndex = argument.Index;

                var (newParamNode, newSeparator) = TransferTrivia(arguments, (T)(UnifiedArgumentSyntax)permutedArguments[newIndex], originalIndex, newIndex, permutedArguments.Count);
                newParamNode = TransferTriviaFromOpenParen<T>(openParenToken, newIndex, originalIndex, newParamNode);

                finalArguments.Add(newParamNode);

                // The last node does't have a separator.
                if (newIndex != permutedArguments.Count - 1)
                {
                    newSeparatorsBuilder.SetItem(newIndex, newSeparator);
                }
            }

            return SyntaxFactory.SeparatedList(finalArguments, newSeparatorsBuilder.ToSyntaxTokenListAndFree());
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
                    if (!content.IsKind(SyntaxKind.XmlElement, out XmlElementSyntax xmlElement))
                    {
                        updatedNodeList.Add(content);
                        continue;
                    }

                    if (xmlElement.StartTag.Name.ToString() != DocumentationCommentXmlNames.ParameterElementName)
                    {
                        updatedNodeList.Add(content);
                        continue;
                    }

                    // Found a param tag, so insert the next one from the reordered list
                    if (index < permutedParamNodes.Count)
                    {
                        updatedNodeList.Add(permutedParamNodes[index].WithLeadingTrivia(content.GetLeadingTrivia()).WithTrailingTrivia<XmlElementSyntax>(content.GetTrailingTrivia()));
                        index++;
                    }
                    else
                    {
                        // Inspecting a param element that we are deleting but not replacing.
                    }
                }

                var newDocComments = SyntaxFactory.DocumentationCommentTrivia(structuredTrivia.Kind(), SyntaxFactory.List(updatedNodeList.AsEnumerable()));
                newDocComments = newDocComments.WithEndOfComment(structuredTrivia.EndOfComment);
                newDocComments = newDocComments.WithLeadingTrivia(structuredTrivia.GetLeadingTrivia()).WithTrailingTrivia<DocumentationCommentTriviaSyntax>(structuredTrivia.GetTrailingTrivia());
                var newTrivia = SyntaxFactory.Trivia(newDocComments);

                updatedLeadingTrivia.Add(newTrivia);
            }

            return updatedLeadingTrivia;
        }

        private static List<SyntaxToken> GetSeparators<T>(SeparatedSyntaxList<T> arguments, int numSeparatorsToSkip = 0) where T : SyntaxNode
        {
            var separators = new List<SyntaxToken>();
            for (var i = 0; i < arguments.SeparatorCount - numSeparatorsToSkip; i++)
            {
                separators.Add(arguments.GetSeparator(i));
            }

            return separators;
        }

        public override async Task<ImmutableArray<SymbolAndProjectId>> DetermineCascadedSymbolsFromDelegateInvokeAsync(
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
    }
}
