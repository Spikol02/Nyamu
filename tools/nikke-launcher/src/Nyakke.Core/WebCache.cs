// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Nyakke.Core;

public sealed record CacheMove(string DataDir, string? MovedTo, bool HadTraces, string? Error);

/// <summary>
/// The official launcher's web cache (tbs_cache). Scripts cached before the November 2026 fix can
/// sit there for months, so Nyakke moves the folder aside; the launcher then fetches fresh copies.
/// Nothing is deleted except backups Nyakke made itself.
/// </summary>
public static class WebCache
{
    public const string CacheDirName = "tbs_cache";
    private const string BackupPrefix = CacheDirName + ".nyakke-";
    private const string EvidencePrefix = CacheDirName + ".evidence-";

    public static bool Exists(string dataDir) => Directory.Exists(Path.Combine(dataDir, CacheDirName));

    public static CacheMove MoveAside(string dataDir, DateTime utcNow, int keepBackups)
    {
        var cache = Path.Combine(dataDir, CacheDirName);
        if (!Directory.Exists(cache))
        {
            return new CacheMove(dataDir, null, false, null);
        }
        var hadTraces = TraceScanner.CacheHasKnownTraces(cache);
        var stamp = utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // A cache with known traces is evidence: it gets its own name and is never pruned.
        var target = Path.Combine(dataDir, (hadTraces ? EvidencePrefix : BackupPrefix) + stamp);
        for (var n = 2; Directory.Exists(target); n++)
        {
            target = Path.Combine(dataDir, (hadTraces ? EvidencePrefix : BackupPrefix) + stamp + "-" + n);
        }
        try
        {
            Directory.Move(cache, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new CacheMove(dataDir, null, hadTraces, e.Message);
        }
        Prune(dataDir, keepBackups);
        return new CacheMove(dataDir, target, hadTraces, null);
    }

    public static List<string> Backups(string dataDir)
    {
        try
        {
            return Directory.EnumerateDirectories(dataDir, BackupPrefix + "*")
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Prune(string dataDir, int keep)
    {
        foreach (var old in Backups(dataDir).Skip(Math.Max(0, keep)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
