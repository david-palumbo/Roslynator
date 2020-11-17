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
using Roslynator.CommandLine.Xml;
using Roslynator.Diagnostics;
using Roslynator.Spelling;
using static Roslynator.Logger;

namespace Roslynator.CommandLine
{
    internal class AnalyzeCommand : MSBuildWorkspaceCommand
    {
        public AnalyzeCommand(AnalyzeCommandLineOptions options, DiagnosticSeverity severityLevel, in ProjectFilter projectFilter) : base(projectFilter)
        {
            Options = options;
            SeverityLevel = severityLevel;
        }

        public AnalyzeCommandLineOptions Options { get; }

        public DiagnosticSeverity SeverityLevel { get; }

        public override async Task<CommandResult> ExecuteAsync(ProjectOrSolution projectOrSolution, CancellationToken cancellationToken = default)
        {
            AssemblyResolver.Register();

            var codeAnalyzerOptions = new CodeAnalyzerOptions(
                ignoreAnalyzerReferences: Options.IgnoreAnalyzerReferences,
                ignoreCompilerDiagnostics: Options.IgnoreCompilerDiagnostics,
                reportNotConfigurable: Options.ReportNotConfigurable,
                reportSuppressedDiagnostics: Options.ReportSuppressedDiagnostics,
                logAnalyzerExecutionTime: Options.ExecutionTime,
                severityLevel: SeverityLevel,
                supportedDiagnosticIds: Options.SupportedDiagnostics,
                ignoredDiagnosticIds: Options.IgnoredDiagnostics);

            IEnumerable<AnalyzerAssembly> analyzerAssemblies = Options.AnalyzerAssemblies
                .SelectMany(path => AnalyzerAssemblyLoader.LoadFrom(path, loadFixers: false).Select(info => info.AnalyzerAssembly));

            CultureInfo culture = (Options.Culture != null) ? CultureInfo.GetCultureInfo(Options.Culture) : null;

            string path = typeof(AnalyzeCommand).Assembly.Location;

            SpellingData spellingData = null;

            if (!string.IsNullOrEmpty(path))
            {
                string directoryPath = Path.GetDirectoryName(path);

                string dictionaryPath = Path.Combine(directoryPath, "roslynator.spelling.dictionary.txt");

                if (File.Exists(dictionaryPath))
                {
                    ImmutableHashSet<string> spellingDictionary = File.ReadAllLines(dictionaryPath)
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .Select(f => f.Trim())
                        .ToImmutableHashSet(StringComparer.CurrentCultureIgnoreCase);

                    spellingData = new SpellingData(spellingDictionary);
                }

                string dictionaryPath2 = Path.Combine(directoryPath, "roslynator.spelling.dictionary.custom.txt");

                if (File.Exists(dictionaryPath2))
                {
                    IEnumerable<string> spellingDictionary2 = File.ReadAllLines(dictionaryPath2)
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .Select(f => f.Trim());

                    spellingData = (spellingData != null)
                        ? new SpellingData(ImmutableHashSet.CreateRange(StringComparer.CurrentCultureIgnoreCase, spellingData.Dictionary.Concat(spellingDictionary2)))
                        : new SpellingData(spellingDictionary2.ToImmutableHashSet(StringComparer.CurrentCultureIgnoreCase));
                }
            }

            var codeAnalyzer = new CodeAnalyzer(
                analyzerAssemblies: analyzerAssemblies,
                formatProvider: culture,
                spellingData: spellingData,
                options: codeAnalyzerOptions);

            if (projectOrSolution.IsProject)
            {
                Project project = projectOrSolution.AsProject();

                ProjectAnalysisResult result = await codeAnalyzer.AnalyzeProjectAsync(project, cancellationToken);

                if (Options.Output != null
                    && result.Diagnostics.Any())
                {
                    DiagnosticXmlSerializer.Serialize(result, project, Options.Output, culture);
                }
            }
            else
            {
                Solution solution = projectOrSolution.AsSolution();

                var projectFilter = new ProjectFilter(Options.Projects, Options.IgnoredProjects, Language);

                ImmutableArray<ProjectAnalysisResult> results = await codeAnalyzer.AnalyzeSolutionAsync(solution, f => projectFilter.IsMatch(f), cancellationToken);

                if (Options.Output != null
                    && results.Any(f => f.Diagnostics.Any()))
                {
                    DiagnosticXmlSerializer.Serialize(results, solution, Options.Output, culture);
                }
            }

            return CommandResult.Success;
        }

        protected override void OperationCanceled(OperationCanceledException ex)
        {
            WriteLine("Analysis was canceled.", Verbosity.Quiet);
        }
    }
}
