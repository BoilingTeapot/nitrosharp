﻿// Essentially a trimmed-down version of https://github.com/dotnet/roslyn/blob/master/src/Compilers/Core/Portable/InternalUtilities/StringTable.cs
// Roslyn is an open-source project by Microsoft licensed under the Apache License, Version 2.0
// See https://github.com/dotnet/roslyn/blob/master/License.txt
// Modifications made by: @SomeAnonDev.

using System;
using System.Threading;

namespace NitroSharp.Utilities
{
    internal sealed class StringInternTable
    {
        private struct Entry
        {
            public int HashCode;
            public string Text;
        }

        // Size of local cache.
        private const int LocalSizeBits = 10;
        private const int LocalSize = (1 << LocalSizeBits);
        private const int LocalSizeMask = LocalSize - 1;

        // max size of shared cache.
        private const int SharedSizeBits = 12;
        private const int SharedSize = (1 << SharedSizeBits);
        private const int SharedSizeMask = SharedSize - 1;

        // size of bucket in shared cache. (local cache has bucket size 1).
        private const int SharedBucketBits = 4;
        private const int SharedBucketSize = (1 << SharedBucketBits);
        private const int SharedBucketSizeMask = SharedBucketSize - 1;

        // local (L1) cache
        // simple fast and not threadsafe cache
        // with limited size and "last add wins" expiration policy
        //
        // The main purpose of the local cache is to use in long lived
        // single threaded operations with lots of locality (like parsing).
        // Local cache is smaller (and thus faster) and is not affected
        // by cache misses on other threads.
        private readonly Entry[] _localTable = new Entry[LocalSize];

        // shared (L2) threadsafe cache
        // slightly slower than local cache
        // we read this cache when having a miss in local cache
        // writes to local cache will update shared cache as well.
        private static readonly Entry[] s_sharedTable = new Entry[SharedSize];

        // essentially a random number
        // the usage pattern will randomly use and increment this
        // the counter is not static to avoid interlocked operations and cross-thread traffic
        private int _localRandom = Environment.TickCount;

        public string Add(ReadOnlySpan<char> span)
        {
            int hashCode = FnvHasher.HashString(span);
            Entry[] entires = _localTable;
            int idx = LocalIdxFromHash(hashCode);

            string text = entires[idx].Text;
            if (text != null && entires[idx].HashCode == hashCode)
            {
                string result = entires[idx].Text;
                if (TextEquals(result, span))
                {
                    return result;
                }
            }

            string? shared = FindSharedEntry(span, hashCode);
            if (shared != null)
            {
                // PERF: the following code does element-wise assignment of a struct
                //       because current JIT produces better code compared to
                //       arr[idx] = new Entry(...)
                entires[idx].HashCode = hashCode;
                entires[idx].Text = shared;

                return shared;
            }

            return AddItem(span, hashCode);
        }

        private static string? FindSharedEntry(ReadOnlySpan<char> span, int hashCode)
        {
            Entry[] arr = s_sharedTable;
            int idx = SharedIdxFromHash(hashCode);

            string? e = null;
            // we use quadratic probing here
            // bucket positions are (n^2 + n)/2 relative to the masked hashcode
            for (int i = 1; i < SharedBucketSize + 1; i++)
            {
                e = arr[idx].Text;
                int hash = arr[idx].HashCode;

                if (e != null)
                {
                    if (hash == hashCode && TextEquals(e, span))
                    {
                        break;
                    }

                    // this is not e we are looking for
                    e = null;
                }
                else
                {
                    // once we see unfilled entry, the rest of the bucket will be empty
                    break;
                }

                idx = (idx + i) & SharedSizeMask;
            }

            return e;
        }

        private string AddItem(ReadOnlySpan<char> span, int hashCode)
        {
            string text = span.ToString();
            AddCore(text, hashCode);
            return text;
        }

        private void AddCore(string chars, int hashCode)
        {
            // add to the shared table first (in case someone looks for same item)
            AddSharedEntry(hashCode, chars);

            // add to the local table too
            Entry[] arr = _localTable;
            int idx = LocalIdxFromHash(hashCode);
            arr[idx].HashCode = hashCode;
            arr[idx].Text = chars;
        }

        private void AddSharedEntry(int hashCode, string text)
        {
            Entry[] arr = s_sharedTable;
            int idx = SharedIdxFromHash(hashCode);

            // try finding an empty spot in the bucket
            // we use quadratic probing here
            // bucket positions are (n^2 + n)/2 relative to the masked hashcode
            int curIdx = idx;
            for (int i = 1; i < SharedBucketSize + 1; i++)
            {
                if (arr[curIdx].Text == null)
                {
                    idx = curIdx;
                    goto foundIdx;
                }

                curIdx = (curIdx + i) & SharedSizeMask;
            }

            // or pick a random victim within the bucket range
            // and replace with new entry
            int i1 = LocalNextRandom() & SharedBucketSizeMask;
            idx = (idx + ((i1 * i1 + i1) / 2)) & SharedSizeMask;

        foundIdx:
            arr[idx].HashCode = hashCode;
            Volatile.Write(ref arr[idx].Text, text);
        }

        private static int LocalIdxFromHash(int hash)
        {
            return hash & LocalSizeMask;
        }

        private static int SharedIdxFromHash(int hash)
        {
            // we can afford to mix some more hash bits here
            return (hash ^ (hash >> LocalSizeBits)) & SharedSizeMask;
        }

        private int LocalNextRandom()
        {
            return _localRandom++;
        }

        internal static bool TextEquals(string array, ReadOnlySpan<char> text)
            => text.Equals(array.AsSpan(), StringComparison.Ordinal);
    }
}
