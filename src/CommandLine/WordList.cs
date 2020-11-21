﻿// Copyright (c) Josef Pihrt. All rights reserved. Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Roslynator.CommandLine
{
    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    internal class WordList
    {
        public WordList(string path, StringComparer comparer, IEnumerable<string> values)
        {
            Path = path;
            Comparer = comparer ?? StringComparer.CurrentCultureIgnoreCase;
            Values = values.ToImmutableHashSet(comparer);
        }

        public string Path { get; }

        public StringComparer Comparer { get; }

        public ImmutableHashSet<string> Values { get; }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string DebuggerDisplay => $"Count = {Values.Count}  {Path}";

        public static WordList Load(string path, StringComparer comparer = null)
        {
            IEnumerable<string> values = ReadWords(path);

            return new WordList(path, comparer, values);
        }

        public static WordList LoadText(string text, StringComparer comparer = null)
        {
            IEnumerable<string> values = ReadLines();

            values = ReadWords(values);

            return new WordList(null, comparer, values);

            IEnumerable<string> ReadLines()
            {
                using (var sr = new StringReader(text))
                {
                    string line;

                    while ((line = sr.ReadLine()) != null)
                        yield return line;
                }
            }
        }

        private static IEnumerable<string> ReadWords(string path)
        {
            return ReadWords(File.ReadLines(path));
        }

        private static IEnumerable<string> ReadWords(IEnumerable<string> values)
        {
            return values
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim());
        }

        public WordList Intersect(WordList wordList, params WordList[] additionalWordList)
        {
            IEnumerable<string> intersect = Values
                .Intersect(wordList.Values, Comparer);

            if (additionalWordList?.Length > 0)
            {
                intersect = intersect
                    .Intersect(additionalWordList.SelectMany(f => f.Values), Comparer);
            }

            return WithValues(intersect);
        }

        public WordList Except(WordList wordList, params WordList[] additionalWordList)
        {
            IEnumerable<string> except = Values
                .Except(wordList.Values, Comparer);

            if (additionalWordList?.Length > 0)
            {
                except = except
                    .Except(additionalWordList.SelectMany(f => f.Values), Comparer);
            }

            return WithValues(except);
        }

        public WordList WithValues(IEnumerable<string> values)
        {
            return new WordList(Path, Comparer, values);
        }

        public void Save(string path = null)
        {
            IEnumerable<string> values = Values
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim().ToLower())
                .Distinct(Comparer)
                .OrderBy(f => f, StringComparer.InvariantCulture);

            File.WriteAllText(path ?? Path, string.Join(Environment.NewLine, values));
        }

        public WordList SaveAndLoad()
        {
            Save();

            return Load(Path, Comparer);
        }
    }
}
