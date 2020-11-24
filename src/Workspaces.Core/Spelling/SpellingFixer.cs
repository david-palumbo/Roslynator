﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using static Roslynator.Logger;

namespace Roslynator.Spelling
{
    internal class SpellingFixer
    {
        public SpellingFixer(
            Solution solution,
            SpellingData spellingData,
            IFormatProvider formatProvider = null,
            SpellingFixerOptions options = null)
        {
            Workspace = solution.Workspace;

            SpellingData = spellingData;
            FormatProvider = formatProvider;
            Options = options ?? SpellingFixerOptions.Default;
        }

        public Workspace Workspace { get; }

        public SpellingData SpellingData { get; private set; }

        public IFormatProvider FormatProvider { get; }

        public SpellingFixerOptions Options { get; }

        private Solution CurrentSolution => Workspace.CurrentSolution;

        public async Task FixSolutionAsync(Func<Project, bool> predicate, CancellationToken cancellationToken = default)
        {
            ImmutableArray<ProjectId> projects = CurrentSolution
                .GetProjectDependencyGraph()
                .GetTopologicallySortedProjects(cancellationToken)
                .ToImmutableArray();

            var results = new List<SpellingFixResult>();

            Stopwatch stopwatch = Stopwatch.StartNew();

            TimeSpan lastElapsed = TimeSpan.Zero;

            for (int i = 0; i < projects.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Project project = CurrentSolution.GetProject(projects[i]);

                if (predicate == null || predicate(project))
                {
                    WriteLine($"Fix '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.Cyan, Verbosity.Minimal);

                    SpellingFixResult result = await FixProjectAsync(project, cancellationToken).ConfigureAwait(false);

                    results.Add(result);
                }
                else
                {
                    WriteLine($"Skip '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.DarkGray, Verbosity.Minimal);

                    //results.Add(SpellingFixResult.Skipped);
                }

                TimeSpan elapsed = stopwatch.Elapsed;

                WriteLine($"Done fixing '{project.Name}' in {elapsed - lastElapsed:mm\\:ss\\.ff}", Verbosity.Normal);

                lastElapsed = elapsed;
            }

            stopwatch.Stop();

            WriteLine($"Done fixing solution '{CurrentSolution.FilePath}' in {stopwatch.Elapsed:mm\\:ss\\.ff}", Verbosity.Minimal);
        }

        public async Task<SpellingFixResult> FixProjectAsync(Project project, CancellationToken cancellationToken = default)
        {
            project = CurrentSolution.GetProject(project.Id);

            while (true)
            {
                SpellingAnalysisResult spellingAnalysisResult = await SpellingAnalyzer.AnalyzeSpellingAsync(
                    project,
                    SpellingData,
                    new SpellingFixerOptions(includeLocal: false),
                    cancellationToken)
                    .ConfigureAwait(false);

                if (!spellingAnalysisResult.Errors.Any())
                    return default;

                var applyChanges = false;

                project = CurrentSolution.GetProject(project.Id);

                Document document = null;

                foreach (IGrouping<SyntaxTree, SpellingError> grouping in spellingAnalysisResult.Errors
                    .Where(f => f.Identifier.Parent == null)
                    .GroupBy(f => f.Location.SourceTree))
                {
                    document = project.GetDocument(grouping.Key);

                    SourceText sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

                    TextLineCollection lines = sourceText.Lines;

                    List<TextChange> textChanges = null;

                    foreach (SpellingError spellingError in grouping.OrderBy(f => f.Location.SourceSpan.Start))
                    {
                        TextLine line = lines.GetLineFromPosition(spellingError.Location.SourceSpan.Start);

                        Write("    ", Verbosity.Normal);
                        WriteLine(line.ToString(), ConsoleColor.DarkGray, Verbosity.Normal);

                        LogHelpers.WriteSpellingError(spellingError, Path.GetDirectoryName(project.FilePath), "    ", Verbosity.Normal);

                        if (SpellingData.IgnoreList.Contains(spellingError.Value))
                            continue;

                        string fix = GetFix(spellingError);

                        if (!string.IsNullOrEmpty(fix)
                            && !string.Equals(fix, spellingError.Value, StringComparison.Ordinal))
                        {
                            (textChanges ??= new List<TextChange>()).Add(new TextChange(spellingError.Location.SourceSpan, fix));

                            //TODO: del
                            if (spellingError.Value != spellingError.ContainingValue)
                            {
                            }

                            ProcessFix(spellingError, fix);
                        }
                        else
                        {
                            SpellingData = SpellingData.AddIgnoredValue(spellingError.Value);
                        }
                    }

                    if (textChanges != null)
                    {
                        document = await document.WithTextChangesAsync(textChanges, cancellationToken).ConfigureAwait(false);
                        project = document.Project;

                        applyChanges = true;
                    }
                }

                if (applyChanges
                    && !Workspace.TryApplyChanges(project.Solution))
                {
                    Debug.Fail($"Cannot apply changes to solution '{project.Solution.FilePath}'");
                    WriteLine($"Cannot apply changes to solution '{project.Solution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                    return default;
                }

                foreach (SpellingError spellingError in spellingAnalysisResult.Errors
                    .Where(f => f.Identifier.Parent != null))
                {
                    document = project.GetDocument(spellingError.Location.SourceTree);

                    if (document != null)
                    {
                        SourceText sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

                        TextLineCollection lines = sourceText.Lines;

                        TextLine line = lines.GetLineFromPosition(spellingError.Location.SourceSpan.Start);

                        Write("    ", Verbosity.Normal);
                        WriteLine(line.ToString(), ConsoleColor.DarkGray, Verbosity.Normal);

                        LogHelpers.WriteSpellingError(spellingError, Path.GetDirectoryName(project.FilePath), "    ", Verbosity.Normal);

                        if (SpellingData.IgnoreList.Contains(spellingError.Value))
                            continue;

                        string identifierText = spellingError.Identifier.ValueText;

                        string fix = GetFix(spellingError);

                        //TODO: del
                        //if (!SpellingData.Fixes.TryGetValue(identifierText, out string fix))
                        //{
                        //    if (SpellingData.Fixes.TryGetValue(spellingError.Value, out fix))
                        //    {
                        //        fix = identifierText
                        //            .Remove(spellingError.Index, spellingError.Value.Length)
                        //            .Insert(spellingError.Index, fix);
                        //    }
                        //    else
                        //    {
                        //        fix = GetFix(spellingError);
                        //    }
                        //}

                        if (!string.IsNullOrEmpty(fix)
                            && !string.Equals(fix, identifierText, StringComparison.Ordinal))
                        {
                            project = CurrentSolution.GetProject(project.Id);

                            SemanticModel semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

                            ISymbol symbol = semanticModel.GetDeclaredSymbol(spellingError.Identifier.Parent, cancellationToken);

                            Solution newSolution = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
                                CurrentSolution,
                                symbol,
                                fix,
                                default(Microsoft.CodeAnalysis.Options.OptionSet),
                                cancellationToken)
                                .ConfigureAwait(false);

                            if (!Workspace.TryApplyChanges(newSolution))
                            {
                                Debug.Fail($"Cannot apply changes to solution '{newSolution.FilePath}'");
                                WriteLine($"Cannot apply changes to solution '{newSolution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
                                return default;
                            }

                            ProcessFix(spellingError, fix);
                            break;
                        }
                        else
                        {
                            SpellingData = SpellingData.AddIgnoredValue(spellingError.Value);
                        }
                    }
                }

                project = CurrentSolution.GetProject(project.Id);
            }
        }

        private void ProcessFix(SpellingError spellingError, string fix)
        {
            string fix2 = null;

            string containingValue = spellingError.ContainingValue;

            int index = spellingError.Index;

            if (fix.Length > index
                && string.CompareOrdinal(fix, 0, containingValue, 0, index) == 0)
            {
                int endIndex = index + spellingError.Value.Length;

                int length = containingValue.Length - endIndex;

                if (fix.Length > index + length
                    && string.CompareOrdinal(fix, fix.Length - length, containingValue, endIndex, length) == 0)
                {
                    fix2 = fix.Substring(index, fix.Length - length - index);
                }
            }

            if (fix2 != null)
            {
                SpellingData = SpellingData.AddFix(spellingError.Value, fix2);
                SpellingData = SpellingData.AddWord(fix2);
            }
            else
            {
                SpellingData = SpellingData.AddFix(spellingError.ContainingValue, fix);
            }
        }

        private string GetFix(SpellingError spellingError)
        {
            string value = spellingError.Value;
            string containingValue = spellingError.ContainingValue;

            bool isContained = !string.Equals(value, containingValue, StringComparison.Ordinal);

            if (isContained
                && SpellingData.Fixes.TryGetValue(containingValue, out SpellingFix spellingFix)
                && string.Equals(containingValue, spellingFix.FixedValue, StringComparison.Ordinal))
            {
                return spellingFix.FixedValue;
            }

            TextCasing textCasing = GetTextCasing(value);

            if (SpellingData.Fixes.TryGetValue(value, out spellingFix))
            {
                string fix = spellingFix.FixedValue;

                if (!string.Equals(value, spellingFix.OriginalValue))
                {
                    if (textCasing == TextCasing.Mixed)
                    {
                        fix = null;
                    }
                    else if (GetTextCasing(fix) != TextCasing.Mixed)
                    {
                        fix = SetTextCasing(fix, textCasing);
                    }
                }

                if (fix != null)
                {
                    if (isContained)
                    {
                        fix = containingValue
                            .Remove(spellingError.Index, value.Length)
                            .Insert(spellingError.Index, fix);
                    }

                    return fix;
                }
            }

            if (textCasing == TextCasing.Mixed)
                return null;

            var fixes = new List<string>();

            if (textCasing == TextCasing.Lower
                || textCasing == TextCasing.FirstUpper)
            {
                foreach (int splitIndex in SpellingFixGenerator
                    .GetSplitIndex(value.ToLowerInvariant(), SpellingData)
                    .Take(5))
                {
                    string fix;
                    if (spellingError.Identifier.Parent != null)
                    {
                        fix = value
                            .Remove(splitIndex, 1)
                            .Insert(splitIndex, char.ToUpperInvariant(value[splitIndex]).ToString());
                    }
                    else
                    {
                        fix = value.Insert(splitIndex, " ");
                    }

                    fixes.Add(fix);
                }
            }

            using (IEnumerator<string> en = SpellingFixGenerator
                .GeneratePossibleFixes(spellingError, SpellingData)
                .GetEnumerator())
            {
                if (en.MoveNext())
                {
                    fixes.Add(en.Current);

                    while (en.MoveNext()
                        && fixes.Count < 5)
                    {
                        if (!fixes.Contains(en.Current, StringComparer.CurrentCultureIgnoreCase))
                            fixes.Add(en.Current);
                    }
                }
            }

            for (int i = 0; i < fixes.Count; i++)
            {
                string fix = fixes[i];

                if (GetTextCasing(fix) != TextCasing.Mixed)
                {
                    fix = SetTextCasing(fixes[i], textCasing);
                    fixes[i] = fix;
                }

                Console.Write("    Fix suggestion");

                if (Options.Interactive)
                    Console.Write($" ({i + 1})");

                Console.Write(": ");
                Console.WriteLine(fix);
            }

            if (Options.Interactive
                && fixes.Count > 0)
            {
                Console.Write("    Enter number of fix suggestion: ");

                if (int.TryParse(Console.ReadLine()?.Trim(), out int number)
                    && number >= 1
                    && number <= fixes.Count)
                {
                    string fix = fixes[number - 1];

                    string value2 = spellingError.ContainingValue;

                    if (value != value2)
                    {
                        int endIndex = spellingError.Index + value.Length;

                        fix = value2.Remove(spellingError.Index)
                            + fix
                            + value2.Substring(endIndex, value2.Length - endIndex);
                    }

                    return fix;
                }
            }

            if (Options.Interactive)
            {
                Console.Write("    Enter fixed value: ");

                return Console.ReadLine()?.Trim();
            }

            return null;
        }

        private static string SetTextCasing(string s, TextCasing textCasing)
        {
            TextCasing textCasing2 = GetTextCasing(s);

            if (textCasing == textCasing2)
                return s;

            switch (textCasing)
            {
                case TextCasing.Lower:
                    return s.ToLowerInvariant();
                case TextCasing.Upper:
                    return s.ToUpperInvariant();
                case TextCasing.FirstUpper:
                    return s.Substring(0, 1).ToUpperInvariant() + s.Substring(1).ToLowerInvariant();
                default:
                    throw new InvalidOperationException($"Invalid enum value '{textCasing}'");
            }
        }

        private static TextCasing GetTextCasing(string s)
        {
            char ch = s[0];

            if (char.IsLower(ch))
            {
                for (int i = 1; i < s.Length; i++)
                {
                    if (!char.IsLower(s[i]))
                        return TextCasing.Mixed;
                }

                return TextCasing.Lower;
            }
            else if (char.IsUpper(ch))
            {
                ch = s[1];

                if (char.IsLower(ch))
                {
                    for (int i = 2; i < s.Length; i++)
                    {
                        if (!char.IsLower(s[i]))
                            return TextCasing.Mixed;
                    }

                    return TextCasing.FirstUpper;
                }
                else if (char.IsUpper(ch))
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (!char.IsUpper(s[i]))
                            return TextCasing.Mixed;
                    }

                    return TextCasing.Upper;
                }
            }

            return TextCasing.Mixed;
        }

        private enum TextCasing
        {
            Mixed,
            Lower,
            Upper,
            FirstUpper,
        }
    }
}
