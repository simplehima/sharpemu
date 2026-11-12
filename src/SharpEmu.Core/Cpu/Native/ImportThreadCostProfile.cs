// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// SHARPEMU_PROFILE_THREAD_HLE=1: per guest thread, how much wall time is spent inside HLE imports
/// and which imports cost the most. Unlike SHARPEMU_PERF_HLE this takes no dictionary lock per call,
/// so it does not change the frame rate of the game being measured. Whatever a busy thread does not
/// spend in HLE is time in guest code.
/// </summary>
internal static class ImportThreadCostProfile
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_THREAD_HLE"), "1", StringComparison.Ordinal);

    private sealed class ThreadCosts
    {
        public string Name = "";
        public long TotalTicks;
        public long Calls;
        public readonly Dictionary<int, (long Ticks, long Calls)> PerImport = new();
        public readonly object Gate = new();
    }

    private static readonly ConcurrentDictionary<int, ThreadCosts> _threads = new();
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;

    [ThreadStatic]
    private static ThreadCosts? _mine;

    internal static void Record(Func<int, string> nameOf, int importIndex, long ticks)
    {
        var costs = _mine ??= _threads.GetOrAdd(Environment.CurrentManagedThreadId, _ => new ThreadCosts());
        lock (costs.Gate)
        {
            costs.Name = Thread.CurrentThread.Name ?? costs.Name;
            costs.TotalTicks += ticks;
            costs.Calls++;
            costs.PerImport.TryGetValue(importIndex, out var entry);
            costs.PerImport[importIndex] = (entry.Ticks + ticks, entry.Calls + 1);
        }

        var now = Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _windowStart);
        if (now - started < Stopwatch.Frequency * 5 || Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _windowStart, now);
            var seconds = (now - started) / (double)Stopwatch.Frequency;
            foreach (var pair in _threads.OrderByDescending(p => p.Value.TotalTicks).Take(4))
            {
                var thread = pair.Value;
                List<KeyValuePair<int, (long Ticks, long Calls)>> top;
                long total;
                long calls;
                lock (thread.Gate)
                {
                    total = thread.TotalTicks;
                    calls = thread.Calls;
                    thread.TotalTicks = 0;
                    thread.Calls = 0;
                    top = thread.PerImport.OrderByDescending(p => p.Value.Ticks).Take(8).ToList();
                    thread.PerImport.Clear();
                }

                if (total == 0)
                {
                    continue;
                }

                var text = string.Join(" | ", top.Select(p =>
                {
                    var name = nameOf(p.Key);
                    return $"{name}={p.Value.Ticks * 1000.0 / Stopwatch.Frequency:F0}ms/n{p.Value.Calls}";
                }));
                Console.Error.WriteLine(
                    $"[PERF][THREAD_HLE] thread='{thread.Name}' managed={pair.Key} window={seconds:F1}s " +
                    $"in_hle={total * 100.0 / Stopwatch.Frequency / seconds:F1}% calls={calls} {text}");
            }
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
