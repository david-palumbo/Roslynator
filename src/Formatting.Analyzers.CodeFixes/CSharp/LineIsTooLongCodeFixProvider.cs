﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Roslynator.CSharp;
using Roslynator.Formatting.CodeFixes.CSharp;
using Roslynator.Formatting.CSharp;

namespace Roslynator.Formatting.CodeFixes
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LineIsTooLongCodeFixProvider))]
    [Shared]
    internal class LineIsTooLongCodeFixProvider : BaseCodeFixProvider
    {
        private const string Title = "Wrap line";

        public sealed override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(DiagnosticIdentifiers.LineIsTooLong);

        private static readonly SyntaxKind[] _parameterListKinds = new[]
        {
            SyntaxKind.ParameterList,
            SyntaxKind.BracketedParameterList,
        };

        private static readonly SyntaxKind[] _memberExpressionKinds = new[]
        {
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.MemberBindingExpression,
        };

        private static readonly SyntaxKind[] _initializerKinds = new[]
        {
            SyntaxKind.ArrayInitializerExpression,
            SyntaxKind.CollectionInitializerExpression,
            SyntaxKind.ComplexElementInitializerExpression,
            SyntaxKind.ObjectInitializerExpression,
        };

        private static readonly SyntaxKind[] _binaryExpressionKinds = new[]
        {
            SyntaxKind.AddExpression,
            SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideExpression,
            SyntaxKind.ModuloExpression,
            SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.BitwiseOrExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.CoalesceExpression,
        };


        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            SyntaxNode root = await context.GetSyntaxRootAsync().ConfigureAwait(false);

            TextSpan span = context.Span;
            Document document = context.Document;
            Diagnostic diagnostic = context.Diagnostics[0];
            string indentation = null;
            int maxLength = AnalyzerSettings.Current.MaxLineLength;
            int position = span.End;

            Dictionary<SyntaxKind, SyntaxNode> spans = null;

            while (position >= span.Start)
            {
                SyntaxToken token = root.FindToken(position);

                SyntaxNode node = token.Parent;

                for (; node?.SpanStart >= span.Start; node = node.Parent)
                {
                    SyntaxKind kind = node.Kind();

                    if (spans != null
                        && spans.TryGetValue(kind, out SyntaxNode node2)
                        && object.ReferenceEquals(node, node2))
                    {
                        continue;
                    }

                    if (kind == SyntaxKind.ArrowExpressionClause)
                    {
                        var expressionBody = (ArrowExpressionClauseSyntax)node;

                        SyntaxToken arrowToken = expressionBody.ArrowToken;
                        SyntaxToken previousToken = arrowToken.GetPreviousToken();

                        if (previousToken.SpanStart < span.Start)
                            continue;

                        bool addNewLineAfter = document.IsAnalyzerOptionEnabled(
                            AnalyzerOptions.AddNewLineAfterExpressionBodyArrowInsteadOfBeforeIt);

                        int wrapPosition = (addNewLineAfter) ? arrowToken.Span.End : previousToken.Span.End;
                        int start = (addNewLineAfter) ? expressionBody.Expression.SpanStart : arrowToken.SpanStart;
                        int longestLength = expressionBody.GetLastToken().GetNextToken().Span.End - start;

                        if (!CanWrapNode(expressionBody, wrapPosition, longestLength))
                            continue;

                        AddSpan(expressionBody);
                        break;
                    }
                    else if (kind == SyntaxKind.ParameterList)
                    {
                        if (node.Parent is AnonymousFunctionExpressionSyntax)
                            continue;

                        if (!VerifyKind(node, _parameterListKinds))
                            continue;

                        var parameterList = (ParameterListSyntax)node;

                        if (!CanWrapSeparatedList(parameterList.Parameters, parameterList.OpenParenToken.Span.End))
                            continue;

                        AddSpan(parameterList);
                        break;
                    }
                    else if (kind == SyntaxKind.BracketedParameterList)
                    {
                        var parameterList = (BracketedParameterListSyntax)node;

                        if (!VerifyKind(node, _parameterListKinds))
                            continue;

                        if (!CanWrapSeparatedList(parameterList.Parameters, parameterList.OpenBracketToken.Span.End))
                            continue;

                        AddSpan(parameterList);
                        break;
                    }
                    else if (kind == SyntaxKind.ArgumentList)
                    {
                        var argumentList = (ArgumentListSyntax)node;

                        if (!CanWrapSeparatedList(argumentList.Arguments, argumentList.OpenParenToken.Span.End))
                            continue;

                        if (!CanWrapLine(argumentList.Parent))
                            continue;

                        AddSpan(argumentList);
                        break;
                    }
                    else if (kind == SyntaxKind.SimpleMemberAccessExpression)
                    {
                        if (!node.IsParentKind(SyntaxKind.InvocationExpression, SyntaxKind.ElementAccessExpression))
                            continue;

                        if (!VerifyKind(node, _memberExpressionKinds))
                            continue;

                        var memberAccessExpression = (MemberAccessExpressionSyntax)node;
                        SyntaxToken dotToken = memberAccessExpression.OperatorToken;

                        if (!CanWrapNode(memberAccessExpression, dotToken.SpanStart, span.End - dotToken.SpanStart))
                            continue;

                        if (!CanWrapLine(memberAccessExpression))
                            continue;

                        AddSpan(memberAccessExpression);
                        break;
                    }
                    else if (kind == SyntaxKind.MemberBindingExpression)
                    {
                        if (!node.IsParentKind(SyntaxKind.InvocationExpression, SyntaxKind.ElementAccessExpression))
                            continue;

                        if (!VerifyKind(node, _memberExpressionKinds))
                            continue;

                        var memberBindingExpression = (MemberBindingExpressionSyntax)node;
                        SyntaxToken dotToken = memberBindingExpression.OperatorToken;

                        if (!CanWrapNode(memberBindingExpression, dotToken.SpanStart, span.End - dotToken.SpanStart))
                            continue;

                        if (!CanWrapLine(memberBindingExpression))
                            continue;

                        AddSpan(memberBindingExpression);
                        break;
                    }
                    else if (kind == SyntaxKind.ConditionalExpression)
                    {
                        //TODO: 
                        break;
                    }

                    switch (kind)
                    {
                        case SyntaxKind.ArrayInitializerExpression:
                        case SyntaxKind.CollectionInitializerExpression:
                        case SyntaxKind.ComplexElementInitializerExpression:
                        case SyntaxKind.ObjectInitializerExpression:
                            {
                                if (!VerifyKind(node, _initializerKinds))
                                    continue;

                                var initializer = (InitializerExpressionSyntax)node;

                                if (!CanWrapSeparatedList(initializer.Expressions, initializer.OpenBraceToken.Span.End))
                                    continue;

                                if (!CanWrapLine(initializer))
                                    continue;

                                AddSpan(initializer);
                                break;
                            }
                        case SyntaxKind.AddExpression:
                        case SyntaxKind.SubtractExpression:
                        case SyntaxKind.MultiplyExpression:
                        case SyntaxKind.DivideExpression:
                        case SyntaxKind.ModuloExpression:
                        case SyntaxKind.LeftShiftExpression:
                        case SyntaxKind.RightShiftExpression:
                        case SyntaxKind.LogicalOrExpression:
                        case SyntaxKind.LogicalAndExpression:
                        case SyntaxKind.BitwiseOrExpression:
                        case SyntaxKind.BitwiseAndExpression:
                        case SyntaxKind.ExclusiveOrExpression:
                        case SyntaxKind.CoalesceExpression:
                            {
                                if (!VerifyKind(node, _binaryExpressionKinds))
                                    continue;

                                var binaryExpression = (BinaryExpressionSyntax)node;

                                SyntaxToken operatorToken = binaryExpression.OperatorToken;

                                bool addNewLineAfter = document.IsAnalyzerOptionEnabled(
                                    AnalyzerOptions.AddNewLineAfterBinaryOperatorInsteadOfBeforeIt);

                                int wrapPosition = (addNewLineAfter)
                                    ? operatorToken.Span.End
                                    : binaryExpression.Left.Span.End;

                                int start = (addNewLineAfter) ? binaryExpression.Right.SpanStart : operatorToken.SpanStart;
                                int end = (FindNextExpressionInChain(binaryExpression)?.Span ?? span).End;
                                int longestLength = end - start;

                                if (!CanWrapNode(binaryExpression, wrapPosition, longestLength))
                                    continue;

                                if (!CanWrapLine(binaryExpression))
                                    continue;

                                AddSpan(binaryExpression);
                                break;
                            }
                    }
                }

                position = Math.Min(position, token.FullSpan.Start) - 1;
            }

            if (spans == null)
                return;

            SyntaxNode argumentListOrMemberExpression = ChooseBetweenArgumentListAndMemberExpression();

            if (argumentListOrMemberExpression != null)
            {
                if (argumentListOrMemberExpression.IsKind(SyntaxKind.ArgumentList))
                {
                    if (spans.ContainsKey(SyntaxKind.SimpleMemberAccessExpression))
                    {
                        spans.Remove(SyntaxKind.SimpleMemberAccessExpression);
                    }
                    else if (spans.ContainsKey(SyntaxKind.MemberBindingExpression))
                    {
                        spans.Remove(SyntaxKind.MemberBindingExpression);
                    }
                }
                else
                {
                    spans.Remove(SyntaxKind.ArgumentList);
                }
            }

            SyntaxNode binaryExpression2 = spans
                .Join(_binaryExpressionKinds, f => f.Key, f => f, (f, _) => f.Value)
                .FirstOrDefault();

            if (binaryExpression2 != null)
            {
                if (spans.TryGetValue(SyntaxKind.ArgumentList, out argumentListOrMemberExpression)
                    || spans.TryGetValue(SyntaxKind.SimpleMemberAccessExpression, out argumentListOrMemberExpression)
                    || spans.TryGetValue(SyntaxKind.MemberBindingExpression, out argumentListOrMemberExpression))
                {
                    if (HasPrecedenceOver(argumentListOrMemberExpression, binaryExpression2))
                    {
                        spans.Remove(binaryExpression2.Kind());
                    }
                    else
                    {
                        spans.Remove(argumentListOrMemberExpression.Kind());
                    }
                }
            }

            SyntaxNode nodeToFix = spans
                .Select(f => f.Value)
                .OrderBy(f => f, SyntaxKindComparer.Instance)
                .First();

            CodeAction codeAction = CodeAction.Create(
                Title,
                GetCreateChangedDocument(nodeToFix),
                base.GetEquivalenceKey(diagnostic));

            context.RegisterCodeFix(codeAction, diagnostic);
            return;

            void AddSpan(SyntaxNode node)
            {
                SyntaxKind kind = node.Kind();

                if (spans == null)
                    spans = new Dictionary<SyntaxKind, SyntaxNode>();

                if (!spans.ContainsKey(kind))
                    spans[kind] = node;
            }

            SyntaxNode ChooseBetweenArgumentListAndMemberExpression()
            {
                if (!spans.ContainsKey(SyntaxKind.ArgumentList))
                    return null;

                if (!spans.ContainsKey(SyntaxKind.SimpleMemberAccessExpression)
                    && !spans.ContainsKey(SyntaxKind.MemberBindingExpression))
                {
                    return null;
                }

                SyntaxNode argumentList = spans[SyntaxKind.ArgumentList];

                SyntaxNode memberExpression = null;

                SyntaxNode memberAccess = (spans.ContainsKey(SyntaxKind.SimpleMemberAccessExpression))
                    ? spans[SyntaxKind.SimpleMemberAccessExpression]
                    : null;

                SyntaxNode memberBinding = (spans.ContainsKey(SyntaxKind.MemberBindingExpression))
                    ? spans[SyntaxKind.MemberBindingExpression]
                    : null;

                if (memberAccess != null)
                {
                    if (memberBinding != null)
                    {
                        if (memberAccess.Contains(memberBinding))
                        {
                            memberExpression = memberAccess;
                        }
                        else if (memberBinding.Contains(memberAccess))
                        {
                            memberExpression = memberBinding;
                        }
                        else if (memberAccess.SpanStart > memberBinding.SpanStart)
                        {
                            memberExpression = memberBinding;
                        }
                        else
                        {
                            memberExpression = memberAccess;
                        }
                    }
                    else
                    {
                        memberExpression = memberAccess;
                    }
                }
                else
                {
                    memberExpression = memberBinding;
                }

                if (argumentList.Contains(memberExpression))
                    return argumentList;

                if (memberExpression.Contains(argumentList))
                    return memberExpression;

                if (memberExpression.Span.End == argumentList.SpanStart)
                {
                    ExpressionSyntax expression = null;

                    if (memberExpression.IsKind(SyntaxKind.SimpleMemberAccessExpression))
                    {
                        var memberAccess2 = (MemberAccessExpressionSyntax)memberExpression;
                        expression = memberAccess2.Expression;
                    }
                    else
                    {
                        var memberBinding2 = (MemberBindingExpressionSyntax)memberExpression;

                        if (memberBinding2.Parent is ConditionalAccessExpressionSyntax conditionalAccess)
                            expression = conditionalAccess.Expression;
                    }

                    if (expression is SimpleNameSyntax)
                        return argumentList;

                    if (expression is CastExpressionSyntax castExpression
                        && castExpression.Expression is SimpleNameSyntax)
                    {
                        return argumentList;
                    }
                }

                if (argumentList.SpanStart > memberExpression.SpanStart)
                    return memberExpression;

                return argumentList;
            }

            bool CanWrapSeparatedList<TNode>(
                SeparatedSyntaxList<TNode> nodes,
                int wrapPosition) where TNode : SyntaxNode
            {
                if (!nodes.Any())
                    return false;

                int longestLength = nodes.Max(f => f.Span.Length);

                return CanWrapNode(nodes.First(), wrapPosition, longestLength);
            }

            bool CanWrapNode(
                SyntaxNode node,
                int wrapPosition,
                int longestLength)
            {
                if (wrapPosition - span.Start > maxLength)
                    return false;

                indentation = SyntaxTriviaAnalysis.GetIncreasedIndentation(node);

                return indentation.Length + longestLength <= maxLength;
            }

            static bool CanWrapLine(SyntaxNode node)
            {
                for (SyntaxNode n = node; n != null; n = n.Parent)
                {
                    switch (n)
                    {
                        case MemberDeclarationSyntax _:
                        case StatementSyntax _:
                        case AccessorDeclarationSyntax _:
                            return true;
                        case InterpolationSyntax _:
                            return false;
                    }
                }

                return true;
            }

            Func<CancellationToken, Task<Document>> GetCreateChangedDocument(SyntaxNode node)
            {
                switch (node.Kind())
                {
                    case SyntaxKind.ArrowExpressionClause:
                        return ct => AddNewLineBeforeOrAfterArrowAsync(document, (ArrowExpressionClauseSyntax)node, ct);
                    case SyntaxKind.ParameterList:
                        return ct => SyntaxFormatter.WrapParametersAsync(document, (ParameterListSyntax)node, ct);
                    case SyntaxKind.BracketedParameterList:
                        return ct => SyntaxFormatter.WrapParametersAsync(document, (BracketedParameterListSyntax)node, ct);
                    case SyntaxKind.SimpleMemberAccessExpression:
                    case SyntaxKind.MemberBindingExpression:
                        return ct => CodeFixHelpers.FixCallChainAsync(
                            document,
                            CSharpUtility.GetTopmostExpressionInCallChain((ExpressionSyntax)node),
                            ct);
                    case SyntaxKind.ArgumentList:
                        return ct => SyntaxFormatter.WrapArgumentsAsync(document, (ArgumentListSyntax)node, ct);
                    case SyntaxKind.ArrayInitializerExpression:
                    case SyntaxKind.CollectionInitializerExpression:
                    case SyntaxKind.ComplexElementInitializerExpression:
                    case SyntaxKind.ObjectInitializerExpression:
                        return ct => SyntaxFormatter.ToMultiLineAsync(document, (InitializerExpressionSyntax)node, ct);
                    case SyntaxKind.AddExpression:
                    case SyntaxKind.SubtractExpression:
                    case SyntaxKind.MultiplyExpression:
                    case SyntaxKind.DivideExpression:
                    case SyntaxKind.ModuloExpression:
                    case SyntaxKind.LeftShiftExpression:
                    case SyntaxKind.RightShiftExpression:
                    case SyntaxKind.LogicalOrExpression:
                    case SyntaxKind.LogicalAndExpression:
                    case SyntaxKind.BitwiseOrExpression:
                    case SyntaxKind.BitwiseAndExpression:
                    case SyntaxKind.ExclusiveOrExpression:
                    case SyntaxKind.CoalesceExpression:
                        return ct =>
                        {
                            var binaryExpression = (BinaryExpressionSyntax)node;
                            var binaryExpression2 = (BinaryExpressionSyntax)binaryExpression
                                .WalkUp(f => f.IsKind(binaryExpression.Kind()));

                            return CodeFixHelpers.FixBinaryExpressionAsync(
                                document,
                                binaryExpression2,
                                TextSpan.FromBounds(
                                    binaryExpression.OperatorToken.SpanStart,
                                    binaryExpression2.OperatorToken.Span.End),
                                ct);
                        };
                    default:
                        return null;
                }
            }

            bool VerifyKind(SyntaxNode node, SyntaxKind[] parameterListKinds)
            {
                if (spans == null)
                    return true;

                SyntaxKind kind = node.Kind();

                foreach (SyntaxKind kind2 in parameterListKinds)
                {
                    if (kind == kind2)
                        continue;

                    if (!spans.TryGetValue(kind2, out SyntaxNode node2))
                        continue;

                    if (!HasPrecedenceOver(node, node2))
                        return false;
                }

                return true;
            }

            static bool HasPrecedenceOver(SyntaxNode node1, SyntaxNode node2)
            {
                if (node1.Contains(node2))
                    return true;

                if (node2.Contains(node1))
                    return false;

                if (node1.SpanStart > node2.SpanStart)
                    return false;

                return true;
            }
        }

        private static Task<Document> AddNewLineBeforeOrAfterArrowAsync(
            Document document,
            ArrowExpressionClauseSyntax arrowExpressionClause,
            CancellationToken cancellationToken = default)
        {
            return AddNewLineBeforeOrAfterAsync(
                document,
                arrowExpressionClause.ArrowToken,
                document.IsAnalyzerOptionEnabled(
                    AnalyzerOptions.AddNewLineAfterExpressionBodyArrowInsteadOfBeforeIt),
                cancellationToken);
        }

        private static Task<Document> AddNewLineBeforeOrAfterBinaryOperatorAsync(
            Document document,
            BinaryExpressionSyntax binaryExpression,
            CancellationToken cancellationToken = default)
        {
            return AddNewLineBeforeOrAfterAsync(
                document,
                binaryExpression.OperatorToken,
                document.IsAnalyzerOptionEnabled(
                    AnalyzerOptions.AddNewLineAfterBinaryOperatorInsteadOfBeforeIt),
                cancellationToken);
        }

        private static Task<Document> AddNewLineBeforeOrAfterAsync(
            Document document,
            SyntaxToken token,
            bool addNewLineAfter,
            CancellationToken cancellationToken = default)
        {
            string indentation = SyntaxTriviaAnalysis.GetIncreasedIndentation(token.Parent, cancellationToken);

            return (addNewLineAfter)
                ? CodeFixHelpers.AddNewLineAfterAsync(document, token, indentation, cancellationToken)
                : CodeFixHelpers.AddNewLineBeforeAsync(document, token, indentation, cancellationToken);
        }

        private static ExpressionSyntax FindNextExpressionInChain(BinaryExpressionSyntax binaryExpression)
        {
            if (binaryExpression.Parent.IsKind(binaryExpression.Kind()))
            {
                var binaryExpression2 = (BinaryExpressionSyntax)binaryExpression.Parent;

                if (object.ReferenceEquals(binaryExpression, binaryExpression2.Left))
                {
                    return binaryExpression2.Right;
                }
            }

            return null;
        }

        private class SyntaxKindComparer : IComparer<SyntaxNode>
        {
            public static SyntaxKindComparer Instance { get; } = new SyntaxKindComparer();

            public int Compare(SyntaxNode x, SyntaxNode y)
            {
                if (object.ReferenceEquals(x, y))
                    return 0;

                if (x == null)
                    return -1;

                if (y == null)
                    return 1;

                return GetRank(x.Kind()).CompareTo(GetRank(y.Kind()));
            }

            private static int GetRank(SyntaxKind kind)
            {
                switch (kind)
                {
                    case SyntaxKind.ArrowExpressionClause:
                        return 10;
                    case SyntaxKind.ArrayInitializerExpression:
                    case SyntaxKind.CollectionInitializerExpression:
                    case SyntaxKind.ComplexElementInitializerExpression:
                    case SyntaxKind.ObjectInitializerExpression:
                        return 11;
                    case SyntaxKind.ParameterList:
                        return 20;
                    case SyntaxKind.BracketedParameterList:
                        return 30;
                    case SyntaxKind.AddExpression:
                    case SyntaxKind.SubtractExpression:
                    case SyntaxKind.MultiplyExpression:
                    case SyntaxKind.DivideExpression:
                    case SyntaxKind.ModuloExpression:
                    case SyntaxKind.LeftShiftExpression:
                    case SyntaxKind.RightShiftExpression:
                    case SyntaxKind.LogicalOrExpression:
                    case SyntaxKind.LogicalAndExpression:
                    case SyntaxKind.BitwiseOrExpression:
                    case SyntaxKind.BitwiseAndExpression:
                    case SyntaxKind.ExclusiveOrExpression:
                    case SyntaxKind.CoalesceExpression:
                        return 31;
                    case SyntaxKind.SimpleMemberAccessExpression:
                        return 40;
                    case SyntaxKind.MemberBindingExpression:
                        return 50;
                    case SyntaxKind.ArgumentList:
                        return 60;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
    }
}
