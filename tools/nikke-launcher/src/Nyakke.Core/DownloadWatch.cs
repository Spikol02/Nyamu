// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace Nyakke.Core;

public sealed record TripwireHit(string Path, long Size, DateTime SeenUtc, string HeaderHex);

/// <summary>
/// Watches the launcher's log folder while the launcher is open. In November 2026 the launcher
/// was made to download its attacker's program there (compiled Lua under a ".log" name) and run
/// it. Nothing else should ever put compiled Lua into a log folder, so such a file is a tripwire.
/// </summary>
public sealed class DownloadWatch : IDisposable
{
    private readonly string logDir;
    private readonly Log log;
    private readonly Lock gate = new();
    private readonly HashSet<string> ignored = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<TripwireHit> hits = new();
    private FileSystemWatcher? watcher;
    private DateTime lastPollUtc = DateTime.MinValue;

    public DownloadWatch(string logDir, Log log)
    {
        this.logDir = logDir;
        this.log = log;
    }

    /// <summary>Files that are already there belong to the check before the start, not to this watch.</summary>
    public void Start()
    {
        foreach (var file in Enumerate(recurse: true))
        {
            if (HasBytecodeHeader(file, out _))
            {
                lock (gate)
                {
                    ignored.Add(FileUtil.NormalizeForCompare(file));
                }
            }
        }
        EnsureWatcher();
        lastPollUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Change notifications can be dropped, so the folder is also read every tick: files that
    /// are new or were written since the last look get their first bytes checked.
    /// </summary>
    public void Poll()
    {
        EnsureWatcher();
        var since = lastPollUtc - TimeSpan.FromSeconds(2);
        lastPollUtc = DateTime.UtcNow;
        foreach (var file in Enumerate(recurse: true))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= since || info.CreationTimeUtc >= since)
                {
                    Check(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public bool TryTakeHit(out TripwireHit? hit) => hits.TryDequeue(out hit);

    public void Dispose()
    {
        lock (gate)
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    private IEnumerable<string> Enumerate(bool recurse)
    {
        if (!Directory.Exists(logDir))
        {
            return [];
        }
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(logDir, "*", options).Take(20000).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void EnsureWatcher()
    {
        lock (gate)
        {
            if (watcher is not null || !Directory.Exists(logDir))
            {
                return;
            }
            try
            {
                var created = new FileSystemWatcher(logDir)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                created.Created += (_, e) => Check(e.FullPath);
                created.Changed += (_, e) => Check(e.FullPath);
                created.Renamed += (_, e) => Check(e.FullPath);
                created.Error += (_, e) => log.Write("watch: " + e.GetException().Message);
                created.EnableRaisingEvents = true;
                watcher = created;
                log.Write("watch: watching " + logDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or PlatformNotSupportedException)
            {
                log.Write("watch: could not watch " + logDir + ": " + e.Message);
            }
        }
    }

    private void Check(string file)
    {
        if (!HasBytecodeHeader(file, out var header))
        {
            return;
        }
        lock (gate)
        {
            if (!ignored.Add(FileUtil.NormalizeForCompare(file)))
            {
                return;
            }
        }
        long size = 0;
        try
        {
            size = new FileInfo(file).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        hits.Enqueue(new TripwireHit(file, size, DateTime.UtcNow, header));
    }

    private static bool HasBytecodeHeader(string file, out string headerHex)
    {
        Span<byte> header = stackalloc byte[4];
        var read = FileUtil.ReadHeader(file, header);
        headerHex = read > 0 ? Convert.ToHexString(header[..read]) : "";
        return read >= 3 && TraceScanner.IsLuaJitBytecode(header[..read]);
    }
}
