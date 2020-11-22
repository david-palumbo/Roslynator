﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Roslynator.Formatting;
using Roslynator.Host.Mef;
using Roslynator.Spelling;
using static Roslynator.CodeFixes.CodeFixerHelpers;
using static Roslynator.Logger;

namespace Roslynator.CodeFixes
{
    internal class CodeFixer
    {
        private readonly AnalyzerAssemblyList _analyzerAssemblies = new AnalyzerAssemblyList();

        private readonly AnalyzerAssemblyList _analyzerReferences = new AnalyzerAssemblyList();

        private bool? _interactive;

        public CodeFixer(
            Solution solution,
            IEnumerable<AnalyzerAssembly> analyzerAssemblies = null,
            IFormatProvider formatProvider = null,
            SpellingData spellingData = null,
            CodeFixerOptions options = null)
        {
            Workspace = solution.Workspace;

            if (analyzerAssemblies != null)
                _analyzerAssemblies.AddRange(analyzerAssemblies);

            FormatProvider = formatProvider;
            SpellingData = spellingData;
            Options = options ?? CodeFixerOptions.Default;
        }

        public Workspace Workspace { get; }

        public IFormatProvider FormatProvider { get; }

        public SpellingData SpellingData { get; private set; }

        public CodeFixerOptions Options { get; }

        private Solution CurrentSolution => Workspace.CurrentSolution;

        public async Task FixSolutionAsync(Func<Project, bool> predicate, CancellationToken cancellationToken = default)
        {
            foreach (string id in Options.IgnoredCompilerDiagnosticIds.OrderBy(f => f))
                WriteLine($"Ignore compiler diagnostic '{id}'", Verbosity.Diagnostic);

            foreach (string id in Options.IgnoredDiagnosticIds.OrderBy(f => f))
                WriteLine($"Ignore diagnostic '{id}'", Verbosity.Diagnostic);

            ImmutableArray<ProjectId> projects = CurrentSolution
                .GetProjectDependencyGraph()
                .GetTopologicallySortedProjects(cancellationToken)
                .ToImmutableArray();

            var results = new List<ProjectFixResult>();

            Stopwatch stopwatch = Stopwatch.StartNew();

            TimeSpan lastElapsed = TimeSpan.Zero;

            for (int i = 0; i < projects.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Project project = CurrentSolution.GetProject(projects[i]);

                if (predicate == null || predicate(project))
                {
                    WriteLine($"Fix '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.Cyan, Verbosity.Minimal);

                    ProjectFixResult result = await FixProjectAsync(project, cancellationToken).ConfigureAwait(false);

                    results.Add(result);

                    LogHelpers.WriteFixSummary(
                        result.FixedDiagnostics,
                        result.UnfixedDiagnostics,
                        result.UnfixableDiagnostics,
                        baseDirectoryPath: Path.GetDirectoryName(project.FilePath),
                        indentation: "  ",
                        formatProvider: FormatProvider,
                        verbosity: Verbosity.Detailed);

                    if (result.Kind == ProjectFixKind.CompilerError)
                        break;
                }
                else
                {
                    WriteLine($"Skip '{project.Name}' {$"{i + 1}/{projects.Length}"}", ConsoleColor.DarkGray, Verbosity.Minimal);

                    results.Add(ProjectFixResult.Skipped);
                }

                TimeSpan elapsed = stopwatch.Elapsed;

                WriteLine($"Done fixing '{project.Name}' in {elapsed - lastElapsed:mm\\:ss\\.ff}", Verbosity.Normal);

                lastElapsed = elapsed;
            }

            stopwatch.Stop();

            WriteLine($"Done fixing solution '{CurrentSolution.FilePath}' in {stopwatch.Elapsed:mm\\:ss\\.ff}", Verbosity.Minimal);

            LogHelpers.WriteProjectFixResults(results, Options, FormatProvider);
        }

        public async Task<ProjectFixResult> FixProjectAsync(Project project, CancellationToken cancellationToken = default)
        {
            (ImmutableArray<DiagnosticAnalyzer> analyzers, ImmutableArray<CodeFixProvider> fixers) = CodeAnalysisHelpers.GetAnalyzersAndFixers(
                project: project,
                analyzerAssemblies: _analyzerAssemblies,
                analyzerReferences: _analyzerReferences,
                options: Options);

            ProjectFixResult fixResult = await FixProjectAsync(project, analyzers, fixers, cancellationToken).ConfigureAwait(false);

            Compilation compilation = await CurrentSolution.GetProject(project.Id).GetCompilationAsync(cancellationToken).ConfigureAwait(false);

            Dictionary<string, ImmutableArray<CodeFixProvider>> fixersById = fixers
                .SelectMany(f => f.FixableDiagnosticIds.Select(id => (id, fixer: f)))
                .GroupBy(f => f.id)
                .ToDictionary(f => f.Key, g => g.Select(f => f.fixer).Distinct().ToImmutableArray());

            ImmutableArray<Diagnostic> unfixableDiagnostics = await GetDiagnosticsAsync(
                analyzers,
                fixResult.FixedDiagnostics,
                compilation,
                f => !fixersById.TryGetValue(f.id, out ImmutableArray<CodeFixProvider> fixers2),
                cancellationToken)
                .ConfigureAwait(false);

            ImmutableArray<Diagnostic> unfixedDiagnostics = await GetDiagnosticsAsync(
                analyzers,
                fixResult.FixedDiagnostics.Concat(unfixableDiagnostics),
                compilation,
                f => fixersById.TryGetValue(f.id, out ImmutableArray<CodeFixProvider> fixers2),
                cancellationToken)
                .ConfigureAwait(false);

            int numberOfAddedFileBanners = 0;

            if (Options.FileBannerLines.Any())
                numberOfAddedFileBanners = await AddFileBannerAsync(CurrentSolution.GetProject(project.Id), Options.FileBannerLines, cancellationToken).ConfigureAwait(false);

            ImmutableArray<DocumentId> formattedDocuments = ImmutableArray<DocumentId>.Empty;

            if (Options.Format)
                formattedDocuments = await FormatProjectAsync(CurrentSolution.GetProject(project.Id), cancellationToken).ConfigureAwait(false);

            if (SpellingData != null)
            {
                await FixSpellingAsync(CurrentSolution.GetProject(project.Id), cancellationToken).ConfigureAwait(false);
            }

            return new ProjectFixResult(
                kind: fixResult.Kind,
                fixedDiagnostics: fixResult.FixedDiagnostics,
                unfixedDiagnostics: unfixedDiagnostics,
                unfixableDiagnostics: unfixableDiagnostics,
                analyzers: fixResult.Analyzers,
                fixers: fixResult.Fixers,
                numberOfFormattedDocuments: formattedDocuments.Length,
                numberOfAddedFileBanners: numberOfAddedFileBanners);
        }

        private async Task<ProjectFixResult> FixProjectAsync(
            Project project,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            ImmutableArray<CodeFixProvider> fixers,
            CancellationToken cancellationToken)
        {
            if (!analyzers.Any())
            {
                WriteLine($"  No analyzers found to analyze '{project.Name}'", ConsoleColor.DarkGray, Verbosity.Normal);
                return ProjectFixResult.NoAnalyzers;
            }

            if (!fixers.Any())
            {
                WriteLine($"  No fixers found to fix '{project.Name}'", ConsoleColor.DarkGray, Verbosity.Normal);
                return new ProjectFixResult(ProjectFixKind.NoFixers, analyzers: analyzers, fixers: fixers);
            }

            Dictionary<string, ImmutableArray<CodeFixProvider>> fixersById = GetFixersById(fixers, Options);

            analyzers = analyzers
                .Where(analyzer => analyzer.SupportedDiagnostics.Any(descriptor => fixersById.ContainsKey(descriptor.Id)))
                .ToImmutableArray();

            if (!analyzers.Any())
            {
                WriteLine($"  No fixable analyzers found to analyze '{project.Name}'", ConsoleColor.DarkGray, Verbosity.Normal);
                return new ProjectFixResult(ProjectFixKind.NoFixableAnalyzers, analyzers: analyzers, fixers: fixers);
            }

            Dictionary<string, ImmutableArray<DiagnosticAnalyzer>> analyzersById = GetAnalyzersById(analyzers);

            LogHelpers.WriteUsedAnalyzers(analyzers, project, Options, ConsoleColor.DarkGray, Verbosity.Diagnostic);
            LogHelpers.WriteUsedFixers(fixers, ConsoleColor.DarkGray, Verbosity.Diagnostic);

            ImmutableArray<Diagnostic>.Builder fixedDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            ImmutableArray<Diagnostic> previousDiagnostics = ImmutableArray<Diagnostic>.Empty;
            ImmutableArray<Diagnostic> previousPreviousDiagnostics = ImmutableArray<Diagnostic>.Empty;

            var fixKind = ProjectFixKind.Success;

            for (int iterationCount = 1; ; iterationCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                project = CurrentSolution.GetProject(project.Id);

                WriteLine($"  Compile '{project.Name}'{((iterationCount > 1) ? $" iteration {iterationCount}" : "")}", Verbosity.Normal);

                Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

                if (!VerifyCompilerDiagnostics(compilerDiagnostics, project))
                    return new ProjectFixResult(ProjectFixKind.CompilerError, fixedDiagnostics, analyzers: analyzers, fixers: fixers);

                WriteLine($"  Analyze '{project.Name}'", Verbosity.Normal);

                ImmutableArray<Diagnostic> diagnostics = await compilation.GetAnalyzerDiagnosticsAsync(analyzers, Options.CompilationWithAnalyzersOptions, cancellationToken).ConfigureAwait(false);

                LogHelpers.WriteAnalyzerExceptionDiagnostics(diagnostics);

                diagnostics = GetFixableDiagnostics(diagnostics, compilerDiagnostics);

                int length = diagnostics.Length;

                if (length == 0)
                    break;

                if (length == previousDiagnostics.Length
                    && !diagnostics.Except(previousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    break;
                }

                if (length == previousPreviousDiagnostics.Length
                    && !diagnostics.Except(previousPreviousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    LogHelpers.WriteInfiniteLoopSummary(diagnostics, previousDiagnostics, project, FormatProvider);

                    fixKind = ProjectFixKind.InfiniteLoop;
                    break;
                }

                WriteLine($"  Found {length} {((length == 1) ? "diagnostic" : "diagnostics")} in '{project.Name}'", Verbosity.Normal);

                foreach (DiagnosticDescriptor descriptor in GetSortedDescriptors(diagnostics))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string diagnosticId = descriptor.Id;

                    DiagnosticFixResult result = await FixDiagnosticsAsync(
                        descriptor,
                        (descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Compiler))
                            ? default
                            : analyzersById[diagnosticId],
                        fixersById[diagnosticId],
                        CurrentSolution.GetProject(project.Id),
                        cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Kind == DiagnosticFixKind.Success)
                    {
                        fixedDiagnostics.AddRange(result.FixedDiagnostics);
                    }
                    else if (result.Kind == DiagnosticFixKind.CompilerError)
                    {
                        return new ProjectFixResult(ProjectFixKind.CompilerError, fixedDiagnostics, analyzers: analyzers, fixers: fixers);
                    }
                }

                if (iterationCount == Options.MaxIterations)
                    break;

                previousPreviousDiagnostics = previousDiagnostics;
                previousDiagnostics = diagnostics;
            }

            return new ProjectFixResult(fixKind, fixedDiagnostics, analyzers: analyzers, fixers: fixers);

            ImmutableArray<Diagnostic> GetFixableDiagnostics(
                ImmutableArray<Diagnostic> diagnostics,
                ImmutableArray<Diagnostic> compilerDiagnostics)
            {
                IEnumerable<Diagnostic> fixableCompilerDiagnostics = compilerDiagnostics
                    .Where(f => f.Severity != DiagnosticSeverity.Error
                        && !Options.IgnoredCompilerDiagnosticIds.Contains(f.Id)
                        && fixersById.ContainsKey(f.Id));

                return diagnostics
                    .Where(f => Options.IsSupportedDiagnostic(f)
                        && analyzersById.ContainsKey(f.Id)
                        && fixersById.ContainsKey(f.Id))
                    .Concat(fixableCompilerDiagnostics)
                    .ToImmutableArray();
            }

            IEnumerable<DiagnosticDescriptor> GetSortedDescriptors(
                ImmutableArray<Diagnostic> diagnostics)
            {
                Dictionary<DiagnosticDescriptor, int> countByDescriptor = diagnostics
                    .GroupBy(f => f.Descriptor, DiagnosticDescriptorComparer.Id)
                    .ToDictionary(f => f.Key, f => f.Count());

                return countByDescriptor
                    .Select(f => f.Key)
                    .OrderBy(f => f, new DiagnosticDescriptorFixComparer(countByDescriptor, fixersById));
            }
        }

        private async Task<DiagnosticFixResult> FixDiagnosticsAsync(
            DiagnosticDescriptor descriptor,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            ImmutableArray<CodeFixProvider> fixers,
            Project project,
            CancellationToken cancellationToken)
        {
            ImmutableArray<Diagnostic>.Builder fixedDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            ImmutableArray<Diagnostic> diagnostics = ImmutableArray<Diagnostic>.Empty;
            ImmutableArray<Diagnostic> previousDiagnostics = ImmutableArray<Diagnostic>.Empty;
            ImmutableArray<Diagnostic> previousDiagnosticsToFix = ImmutableArray<Diagnostic>.Empty;

            int length = 0;
            var fixKind = DiagnosticFixKind.NotFixed;

            while (true)
            {
                Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);

                ImmutableArray<Diagnostic> compilerDiagnostics = compilation.GetDiagnostics(cancellationToken);

                if (!VerifyCompilerDiagnostics(compilerDiagnostics, project))
                {
                    fixKind = DiagnosticFixKind.CompilerError;

                    if (!previousDiagnostics.Any())
                        break;
                }

                if (analyzers.IsDefault)
                {
                    diagnostics = compilerDiagnostics;
                }
                else
                {
                    diagnostics = await compilation.GetAnalyzerDiagnosticsAsync(analyzers, Options.CompilationWithAnalyzersOptions, cancellationToken).ConfigureAwait(false);
                }

                diagnostics = diagnostics
                    .Where(f => f.Id == descriptor.Id && f.Severity >= Options.SeverityLevel)
                    .ToImmutableArray();

                if (fixKind == DiagnosticFixKind.CompilerError)
                {
                    break;
                }
                else if (fixKind == DiagnosticFixKind.Success)
                {
                    if (Options.BatchSize <= 0
                        || length <= Options.BatchSize)
                    {
                        break;
                    }
                }
                else if (previousDiagnostics.Any()
                    && fixKind != DiagnosticFixKind.PartiallyFixed)
                {
                    break;
                }

                length = diagnostics.Length;

                if (length == 0)
                    break;

                if (length == previousDiagnostics.Length
                    && !diagnostics.Except(previousDiagnostics, DiagnosticDeepEqualityComparer.Instance).Any())
                {
                    break;
                }

                fixedDiagnostics.AddRange(previousDiagnosticsToFix.Except(diagnostics, DiagnosticDeepEqualityComparer.Instance));

                previousDiagnostics = diagnostics;

                if (Options.BatchSize > 0
                    && length > Options.BatchSize)
                {
                    diagnostics = ImmutableArray.CreateRange(diagnostics, 0, Options.BatchSize, f => f);
                }

                fixKind = await FixDiagnosticsAsync(diagnostics, descriptor, fixers, project, cancellationToken).ConfigureAwait(false);

                previousDiagnosticsToFix = diagnostics;

                project = CurrentSolution.GetProject(project.Id);
            }

            fixedDiagnostics.AddRange(previousDiagnosticsToFix.Except(diagnostics, DiagnosticDeepEqualityComparer.Instance));

            return new DiagnosticFixResult(fixKind, fixedDiagnostics.ToImmutableArray());
        }

        private async Task<DiagnosticFixKind> FixDiagnosticsAsync(
            ImmutableArray<Diagnostic> diagnostics,
            DiagnosticDescriptor descriptor,
            ImmutableArray<CodeFixProvider> fixers,
            Project project,
            CancellationToken cancellationToken)
        {
            WriteLine($"  Fix {diagnostics.Length} {descriptor.Id} '{descriptor.Title}'", diagnostics[0].Severity.GetColor(), Verbosity.Normal);

            LogHelpers.WriteDiagnostics(diagnostics, baseDirectoryPath: Path.GetDirectoryName(project.FilePath), formatProvider: FormatProvider, indentation: "    ", verbosity: Verbosity.Detailed);

            DiagnosticFix diagnosticFix = await DiagnosticFixProvider.GetFixAsync(
                diagnostics,
                descriptor,
                fixers,
                project,
                Options,
                FormatProvider,
                cancellationToken)
                .ConfigureAwait(false);

            if (diagnosticFix.FixProvider2 != null)
                return DiagnosticFixKind.MultipleFixers;

            CodeAction fix = diagnosticFix.CodeAction;

            if (fix != null)
            {
                ImmutableArray<CodeActionOperation> operations = await fix.GetOperationsAsync(cancellationToken).ConfigureAwait(false);

                if (operations.Length == 1)
                {
                    operations[0].Apply(Workspace, cancellationToken);

                    return (diagnostics.Length != 1 && diagnosticFix.FixProvider.GetFixAllProvider() == null)
                        ? DiagnosticFixKind.PartiallyFixed
                        : DiagnosticFixKind.Success;
                }
                else if (operations.Length > 1)
                {
                    LogHelpers.WriteMultipleOperationsSummary(fix);
                }
            }

            return DiagnosticFixKind.NotFixed;
        }

        private bool VerifyCompilerDiagnostics(ImmutableArray<Diagnostic> diagnostics, Project project)
        {
            const string indentation = "    ";

            using (IEnumerator<Diagnostic> en = diagnostics
                .Where(f => f.Severity == DiagnosticSeverity.Error
                    && !Options.IgnoredCompilerDiagnosticIds.Contains(f.Id))
                .GetEnumerator())
            {
                if (en.MoveNext())
                {
                    Write(indentation);
                    WriteLine("Compilation errors:");

                    string baseDirectoryPath = Path.GetDirectoryName(project.FilePath);

                    const int maxCount = 10;

                    int count = 0;

                    do
                    {
                        count++;

                        if (count <= maxCount)
                        {
                            LogHelpers.WriteDiagnostic(
                                en.Current,
                                baseDirectoryPath: baseDirectoryPath,
                                formatProvider: FormatProvider,
                                indentation: indentation,
                                verbosity: Verbosity.Normal);
                        }
                        else
                        {
                            break;
                        }

                    } while (en.MoveNext());

                    count = 0;

                    var plus = false;

                    while (en.MoveNext())
                    {
                        count++;

                        if (count == 1000)
                        {
                            plus = true;
                            break;
                        }
                    }

                    if (count > maxCount)
                    {
                        Write(indentation);
                        WriteLine($"and {count}{((plus) ? "+" : "")} more errors", verbosity: Verbosity.Normal);
                    }

                    if (!Options.IgnoreCompilerErrors)
                    {
#if DEBUG
                        Console.Write("Stop (Y/N)? ");

                        if (char.ToUpperInvariant((char)Console.Read()) == 'Y')
                            return false;
#else
                        return false;
#endif
                    }
                }
            }

            return true;
        }

        private async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            IEnumerable<Diagnostic> except,
            Compilation compilation,
            Func<(string id, DiagnosticAnalyzer analyzer), bool> predicate,
            CancellationToken cancellationToken)
        {
            Dictionary<string, ImmutableArray<DiagnosticAnalyzer>> analyzersById = analyzers
                .SelectMany(f => f.SupportedDiagnostics.Select(d => (id: d.Id, analyzer: f)))
                .Where(predicate)
                .GroupBy(f => f.id, f => f.analyzer)
                .ToDictionary(g => g.Key, g => g.Select(analyzer => analyzer).Distinct().ToImmutableArray());

            analyzers = analyzersById
                .SelectMany(f => f.Value)
                .Distinct()
                .ToImmutableArray();

            if (!analyzers.Any())
                return ImmutableArray<Diagnostic>.Empty;

            ImmutableArray<Diagnostic> diagnostics = await compilation.GetAnalyzerDiagnosticsAsync(analyzers, Options.CompilationWithAnalyzersOptions, cancellationToken).ConfigureAwait(false);

            return diagnostics
                .Where(f => Options.IsSupportedDiagnostic(f)
                    && analyzersById.ContainsKey(f.Id))
                .Except(except, DiagnosticDeepEqualityComparer.Instance)
                .ToImmutableArray();
        }

        private async Task<int> AddFileBannerAsync(
            Project project,
            ImmutableArray<string> banner,
            CancellationToken cancellationToken)
        {
            int count = 0;

            string solutionDirectory = Path.GetDirectoryName(project.Solution.FilePath);

            foreach (DocumentId documentId in project.DocumentIds)
            {
                Document document = project.GetDocument(documentId);

                if (GeneratedCodeUtility.IsGeneratedCodeFile(document.FilePath))
                    continue;

                SyntaxNode root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                ISyntaxFactsService syntaxFacts = MefWorkspaceServices.Default.GetService<ISyntaxFactsService>(project.Language);

                if (syntaxFacts.BeginsWithAutoGeneratedComment(root))
                    continue;

                if (syntaxFacts.BeginsWithBanner(root, banner))
                    continue;

                SyntaxTriviaList leading = root.GetLeadingTrivia();

                SyntaxTriviaList newLeading = leading.InsertRange(0, banner.SelectMany(f => syntaxFacts.ParseLeadingTrivia(syntaxFacts.SingleLineCommentStart + f + Environment.NewLine)));

                if (!syntaxFacts.IsEndOfLineTrivia(leading.LastOrDefault()))
                    newLeading = newLeading.AddRange(syntaxFacts.ParseLeadingTrivia(Environment.NewLine));

                SyntaxNode newRoot = root.WithLeadingTrivia(newLeading);

                Document newDocument = document.WithSyntaxRoot(newRoot);

                WriteLine($"  Add banner to '{PathUtilities.TrimStart(document.FilePath, solutionDirectory)}'", ConsoleColor.DarkGray, Verbosity.Detailed);

                project = newDocument.Project;

                count++;
            }

            if (count > 0
                && !Workspace.TryApplyChanges(project.Solution))
            {
                Debug.Fail($"Cannot apply changes to solution '{project.Solution.FilePath}'");
                WriteLine($"Cannot apply changes to solution '{project.Solution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
            }

            return count;
        }

        private async Task<ImmutableArray<DocumentId>> FormatProjectAsync(Project project, CancellationToken cancellationToken)
        {
            WriteLine($"  Format  '{project.Name}'", Verbosity.Normal);

            ISyntaxFactsService syntaxFacts = MefWorkspaceServices.Default.GetService<ISyntaxFactsService>(project.Language);

            Project newProject = await CodeFormatter.FormatProjectAsync(project, syntaxFacts, cancellationToken).ConfigureAwait(false);

            string solutionDirectory = Path.GetDirectoryName(project.Solution.FilePath);

            ImmutableArray<DocumentId> formattedDocuments = await CodeFormatter.GetFormattedDocumentsAsync(project, newProject, syntaxFacts).ConfigureAwait(false);

            LogHelpers.WriteFormattedDocuments(formattedDocuments, project, solutionDirectory);

            if (formattedDocuments.Length > 0
                && !Workspace.TryApplyChanges(newProject.Solution))
            {
                Debug.Fail($"Cannot apply changes to solution '{newProject.Solution.FilePath}'");
                WriteLine($"Cannot apply changes to solution '{newProject.Solution.FilePath}'", ConsoleColor.Yellow, Verbosity.Diagnostic);
            }

            return formattedDocuments;
        }

        private async Task FixSpellingAsync(Project project, CancellationToken cancellationToken)
        {
            WriteLine($"  Fix spelling in '{project.Name}'", Verbosity.Normal);

            if (_interactive == null)
            {
                Console.Write("Interactive?: ");
                _interactive = Console.ReadLine()?.Trim().ToLower() != "n";
            }

            while (true)
            {
                SpellingAnalysisResult spellingAnalysisResult = await SpellingAnalysis.AnalyzeSpellingAsync(
                    project,
                    SpellingData,
                    new SpellingAnalysisOptions(includeLocal: false),
                    cancellationToken)
                    .ConfigureAwait(false);

                if (!spellingAnalysisResult.Errors.Any())
                    return;

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
                        WriteLine(line.ToString(), Verbosity.Normal);

                        LogHelpers.WriteSpellingError(spellingError, Path.GetDirectoryName(project.FilePath), "    ", Verbosity.Normal);

                        if (SpellingData.IgnoreList.Contains(spellingError.Value))
                            continue;

                        if (!SpellingData.Fixes.TryGetValue(spellingError.Value, out string fix)
                            && _interactive == true)
                        {
                            Console.Write("    Enter fixed value: ");

                            fix = Console.ReadLine()?.Trim();
                        }

                        if (!string.IsNullOrEmpty(fix)
                            && !string.Equals(fix, spellingError.Value, StringComparison.Ordinal))
                        {
                            (textChanges ??= new List<TextChange>()).Add(new TextChange(spellingError.Location.SourceSpan, fix));

                            SpellingData = SpellingData.AddFix(spellingError.Value, fix);
                            SpellingData = SpellingData.AddWord(fix);
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
                    return;
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
                        WriteLine(line.ToString(), Verbosity.Normal);

                        LogHelpers.WriteSpellingError(spellingError, Path.GetDirectoryName(project.FilePath), "    ", Verbosity.Normal);

                        if (SpellingData.IgnoreList.Contains(spellingError.Value))
                            continue;

                        string identifierText = spellingError.Identifier.ValueText;

                        if (!SpellingData.Fixes.TryGetValue(identifierText, out string fix))
                        {
                            if (SpellingData.Fixes.TryGetValue(spellingError.Value, out fix))
                            {
                                fix = identifierText
                                    .Remove(spellingError.Index, spellingError.Value.Length)
                                    .Insert(spellingError.Index, fix);
                            }
                            else if (_interactive == true)
                            {
                                Console.Write("    Enter fixed value: ");

                                fix = Console.ReadLine()?.Trim();
                            }
                        }

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
                                return;
                            }

                            string fix2 = null;
                            int index = spellingError.Index;

                            if (fix.Length > index
                                && string.CompareOrdinal(fix, 0, identifierText, 0, index) == 0)
                            {
                                int endIndex = index + spellingError.Value.Length;

                                int length = identifierText.Length - endIndex;

                                if (fix.Length > index + length
                                    && string.CompareOrdinal(fix, fix.Length - length, identifierText, endIndex, length) == 0)
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
                                SpellingData = SpellingData.AddFix(identifierText, fix);
                            }

                            break;
                        }
                        else
                        {
                            SpellingData = SpellingData.AddIgnoredValue(identifierText);
                        }
                    }
                }

                project = CurrentSolution.GetProject(project.Id);
            }
        }
    }
}
