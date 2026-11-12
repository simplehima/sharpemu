// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

/// <summary>
/// SHARPEMU_LOG_CPU_WRITE_READBACKS=1: a CPU write into GPU-dirty guest memory stalls the writing guest
/// thread on a GPU readback. Every five seconds this reports which 64 KiB regions caused those stalls and
/// how many writes and bytes each took, so the writer can be identified.
/// </summary>
internal static class CpuWriteReadbackReport
{
    private const int WindowSeconds = 5;
    private const ulong RegionShift = 16;

    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CPU_WRITE_READBACKS"), "1", StringComparison.Ordinal);

    private static readonly ConcurrentDictionary<ulong, (long Writes, long Bytes, ulong MinAddress, ulong MaxEnd)> _regions = new();
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;

    internal static void Record(ulong address, ulong size)
    {
        if (!Enabled)
        {
            return;
        }

        _regions.AddOrUpdate(
            address >> (int)RegionShift,
            _ => (1, (long)size, address, address + size),
            (_, current) => (current.Writes + 1, current.Bytes + (long)size,
                Math.Min(current.MinAddress, address), Math.Max(current.MaxEnd, address + size)));

        var now = Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _windowStart);
        if (now - started < Stopwatch.Frequency * WindowSeconds || Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _windowStart, now);
            var snapshot = _regions.ToArray();
            _regions.Clear();
            var seconds = (now - started) / (double)Stopwatch.Frequency;
            var writes = snapshot.Sum(pair => pair.Value.Writes);
            var text = string.Join(" | ", snapshot
                .OrderByDescending(pair => pair.Value.Writes)
                .Take(6)
                .Select(pair => $"0x{pair.Value.MinAddress:X}-0x{pair.Value.MaxEnd:X}=n{pair.Value.Writes}/{pair.Value.Bytes}B"));
            Console.Error.WriteLine(
                $"[PERF][CPU_WRITE_READBACK] window={seconds:F1}s writes={writes} regions={snapshot.Length} {text}");
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
