// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Nyakke.Core;

public enum FindingKind
{
    AgentLog,
    LoaderLog,
    PayloadFile,
    PersistenceRegistry,
    PersistenceDll,
    CacheLoader,
    CacheAgentState,
    TempLeftover,
}

public sealed record Finding(FindingKind Kind, string Title, string Detail, string? Path)
{
    /// <summary>Persistence that is in place now and would load again by itself.</summary>
    public bool IsActive => Kind is FindingKind.PersistenceRegistry or FindingKind.PersistenceDll;

    /// <summary>Log lines are tracked by position in the log instead of by key.</summary>
    public bool IsLogLine => Kind is FindingKind.AgentLog or FindingKind.LoaderLog;

    public string Key => Kind + "|" + FileUtil.NormalizeForCompare(Path ?? "");
}

public sealed class ScanRequest
{
    public NikkeInstall? Install { get; init; }

    /// <summary>Existing launcher data folders (%APPDATA%\nikke_launcher and its variants).</summary>
    public IReadOnlyList<string> LauncherDataDirs { get; init; } = [];

    public string? ChromeUserDataDir { get; init; }

    public string? TempDir { get; init; }

    /// <summary>Null when the registry was not looked at.</summary>
    public bool? PersistenceKeyPresent { get; init; }

    /// <summary>Bytes of debug.log that were reviewed before; only later lines are reported.</summary>
    public long DebugLogBaseline { get; init; }
}

public sealed class ScanResult
{
    public List<Finding> Findings { get; } = [];

    /// <summary>Length of debug.log when it was read, or -1 without one.</summary>
    public long DebugLogLength { get; set; } = -1;

    /// <summary>What was looked at, for the report.</summary>
    public List<string> Checked { get; } = [];

    public DateTime FinishedUtc { get; set; }
}

/// <summary>
/// Read-only look for the traces of the November 2026 launcher compromise (the "k2f" agent).
/// It finds what that attack is known to leave behind; a clean result is not proof of a clean PC.
/// </summary>
public static class TraceScanner
{
    /// <summary>Code-cache entries the loader left; the names are the same on every PC.</summary>
    public static readonly string[] LoaderCacheEntries =
        ["dd65fa4d61079e7e_0", "ce2fd3f1007a9fe9_0", "1416c638b3830103_0", "fd520c7c0f65c2d7_0"];

    private const string AgentMarker = "k2f_chan";
    private const string LoaderMarker = "insightjs";
    private const string AgentStateMarker = "k2f.agent";
    private const string PersistenceDllName = "platform_experience_helper.dll";
    private const int MaxLogFiles = 5000;

    public static ScanResult Scan(ScanRequest request, CancellationToken cancel = default)
    {
        var result = new ScanResult();
        if (request.Install is { } install)
        {
            ScanDebugLog(install, request.DebugLogBaseline, result, cancel);
            ScanLogFolder(install, result, cancel);
        }
        foreach (var dataDir in request.LauncherDataDirs)
        {
            cancel.ThrowIfCancellationRequested();
            ScanWebCache(Path.Combine(dataDir, WebCache.CacheDirName), result);
        }
        if (request.PersistenceKeyPresent is { } keyPresent)
        {
            result.Checked.Add(@"HKCU\Software\Classes\CLSID\" + TraceEnvironment.PersistenceClsid);
            if (keyPresent)
            {
                result.Findings.Add(new Finding(FindingKind.PersistenceRegistry,
                    "A registry entry that loads the attacker's DLL is present",
                    "Programs that ask Windows for this component load the DLL. Remove it by following " +
                    "the official manual check, then restart Windows.",
                    @"HKCU\Software\Classes\CLSID\" + TraceEnvironment.PersistenceClsid));
            }
        }
        if (request.ChromeUserDataDir is { } chrome)
        {
            ScanChrome(chrome, result);
        }
        if (request.TempDir is { } temp)
        {
            ScanTemp(temp, result);
        }
        result.FinishedUtc = DateTime.UtcNow;
        return result;
    }

    public static bool IsLuaJitBytecode(ReadOnlySpan<byte> header)
    {
        return header.Length >= 3 && header[0] == 0x1B && header[1] == (byte)'L' && header[2] == (byte)'J';
    }

    /// <summary>True when a tbs_cache folder holds entries the loader or the agent wrote.</summary>
    public static bool CacheHasKnownTraces(string cacheDir)
    {
        var probe = new ScanResult();
        ScanWebCache(cacheDir, probe);
        return probe.Findings.Count > 0;
    }

    private static void ScanDebugLog(NikkeInstall install, long baseline, ScanResult result, CancellationToken cancel)
    {
        var path = install.DebugLog;
        if (path is null)
        {
            result.Checked.Add("debug.log (not there yet)");
            return;
        }
        byte[][] agentNeedles = [Encoding.ASCII.GetBytes(AgentMarker), Encoding.Unicode.GetBytes(AgentMarker)];
        byte[][] loaderNeedles = [Encoding.ASCII.GetBytes(LoaderMarker), Encoding.Unicode.GetBytes(LoaderMarker)];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var length = stream.Length;
            result.DebugLogLength = length;
            // A shorter log than last time is a new log: read all of it.
            var start = baseline > 0 && baseline <= length ? baseline : 0;
            stream.Position = start;
            result.Checked.Add(start > 0
                ? $"{path} (lines added since the last review)"
                : path);

            var overlap = agentNeedles.Concat(loaderNeedles).Max(n => n.Length) - 1;
            var buffer = new byte[(1 << 18) + overlap];
            var carried = 0;
            long windowOffset = start;
            long agentHits = 0, loaderHits = 0, firstAgent = -1, firstLoader = -1;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, carried, buffer.Length - carried);
                if (read <= 0)
                {
                    break;
                }
                var window = buffer.AsSpan(0, carried + read);
                Count(window, carried, windowOffset, agentNeedles, ref agentHits, ref firstAgent);
                Count(window, carried, windowOffset, loaderNeedles, ref loaderHits, ref firstLoader);
                var keep = Math.Min(overlap, window.Length);
                window[^keep..].CopyTo(buffer);
                windowOffset += window.Length - keep;
                carried = keep;
            }
            if (agentHits > 0)
            {
                result.Findings.Add(new Finding(FindingKind.AgentLog,
                    "The launcher log shows the remote-control agent started",
                    $"{agentHits} line(s) with \"{AgentMarker}\", the first at byte {firstAgent}. While the " +
                    "launcher was open, the attacker could run commands and read files on this PC.",
                    path));
            }
            else if (loaderHits > 0)
            {
                result.Findings.Add(new Finding(FindingKind.LoaderLog,
                    "The launcher log mentions the attacker's server",
                    $"{loaderHits} mention(s) of \"{LoaderMarker}\", the first at byte {firstLoader}. The " +
                    "modified launcher scripts ran here; the log does not show the agent itself starting.",
                    path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result.Checked.Add($"{path} (could not be read: {e.Message})");
        }
    }

    /// <summary>Counts matches that end after the bytes carried over from the previous window.</summary>
    private static void Count(ReadOnlySpan<byte> window, int carried, long windowOffset, byte[][] needles,
        ref long hits, ref long first)
    {
        foreach (var needle in needles)
        {
            var from = 0;
            while (from <= window.Length - needle.Length)
            {
                var index = window[from..].IndexOf(needle);
                if (index < 0)
                {
                    break;
                }
                var at = from + index;
                if (at + needle.Length > carried)
                {
                    hits++;
                    var absolute = windowOffset + at;
                    if (first < 0 || absolute < first)
                    {
                        first = absolute;
                    }
                }
                from = at + 1;
            }
        }
    }

    private static void ScanLogFolder(NikkeInstall install, ScanResult result, CancellationToken cancel)
    {
        var logDir = FileUtil.FindChild(install.LauncherDir, "log");
        if (logDir is null || !Directory.Exists(logDir))
        {
            return;
        }
        result.Checked.Add(logDir);
        Span<byte> header = stackalloc byte[4];
        var seen = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(logDir, "*", options))
            {
                cancel.ThrowIfCancellationRequested();
                if (++seen > MaxLogFiles)
                {
                    result.Checked.Add($"{logDir}: stopped after {MaxLogFiles} files");
                    break;
                }
                var name = Path.GetFileName(file);
                var read = FileUtil.ReadHeader(file, header);
                var bytecode = read >= 3 && IsLuaJitBytecode(header[..read]);
                if (bytecode || string.Equals(name, "k2f.lua", StringComparison.OrdinalIgnoreCase))
                {
                    result.Findings.Add(new Finding(FindingKind.PayloadFile,
                        "A program file sits in the launcher's log folder",
                        (bytecode ? "The file starts like compiled Lua (LuaJIT), " : "The file is named like ") +
                        "the agent the launcher was made to download. Keep it as evidence.",
                        file));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ScanWebCache(string cacheDir, ScanResult result)
    {
        if (!Directory.Exists(cacheDir))
        {
            return;
        }
        result.Checked.Add(cacheDir);
        var codeCache = FileUtil.FindPath(cacheDir, "Code Cache", "js");
        if (codeCache is not null)
        {
            foreach (var entry in LoaderCacheEntries)
            {
                var file = FileUtil.FindChild(codeCache, entry);
                if (file is not null)
                {
                    result.Findings.Add(new Finding(FindingKind.CacheLoader,
                        "The launcher's web cache holds an entry of the loader script",
                        "The modified launcher scripts ran on this PC at some point. This entry alone does " +
                        "not show that the agent arrived.",
                        file));
                }
            }
        }
        var storage = FileUtil.FindPath(cacheDir, "Local Storage", "leveldb");
        if (storage is null)
        {
            return;
        }
        var needle = Encoding.ASCII.GetBytes(AgentStateMarker);
        try
        {
            foreach (var file in Directory.EnumerateFiles(storage))
            {
                var info = new FileInfo(file);
                if (info.Length is 0 or > 64 * 1024 * 1024)
                {
                    continue;
                }
                byte[] bytes;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (bytes.AsSpan().IndexOf(needle) >= 0)
                {
                    result.Findings.Add(new Finding(FindingKind.CacheAgentState,
                        "The launcher's web cache holds the agent's saved state",
                        "The agent ran on this PC and registered with its server.",
                        file));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ScanChrome(string chromeUserData, ScanResult result)
    {
        var helper = FileUtil.FindChild(chromeUserData, "PlatformExperienceHelper");
        if (helper is null || !Directory.Exists(helper))
        {
            return;
        }
        result.Checked.Add(helper);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(helper))
            {
                var name = Path.GetFileName(sub);
                // Chrome makes the folder itself; only a 64-hex subfolder with the DLL is the attacker's.
                if (name.Length != 64 || !name.All(char.IsAsciiHexDigit))
                {
                    continue;
                }
                var dll = FileUtil.FindChild(sub, PersistenceDllName);
                if (dll is not null)
                {
                    result.Findings.Add(new Finding(FindingKind.PersistenceDll,
                        "The attacker's DLL is in Chrome's data folder",
                        "It reads passwords typed in Chrome and keeps working after the launcher is closed. " +
                        "Follow the official manual check to remove it; the file is locked until Windows restarts.",
                        dll));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ScanTemp(string tempDir, ScanResult result)
    {
        if (!Directory.Exists(tempDir))
        {
            return;
        }
        result.Checked.Add(Path.Combine(tempDir, "k2f*"));
        string[] patterns = ["k2f-bcreds-*.db", "k2f_screen_*", "k2f-keyscan-*.db"];
        foreach (var pattern in patterns)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(tempDir, pattern))
                {
                    result.Findings.Add(new Finding(FindingKind.TempLeftover,
                        "A leftover file of the agent is in the temp folder",
                        "The agent writes such files while it collects browser data or screenshots.",
                        file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Collects the places to scan for the user running Nyakke.</summary>
public static class TraceEnvironment
{
    public const string PersistenceClsid = "{E77CC89B-7401-4C04-8CED-149DB35ADD04}";

    public static IReadOnlyList<string> LauncherDataDirs(string? appDataDir = null)
    {
        appDataDir ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appDataDir))
        {
            return [];
        }
        return NikkeInstall.AllDataDirNames
            .Select(name => Path.Combine(appDataDir, name))
            .Where(Directory.Exists)
            .ToList();
    }

    public static ScanRequest ForCurrentUser(NikkeInstall? install, long debugLogBaseline)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ScanRequest
        {
            Install = install,
            LauncherDataDirs = LauncherDataDirs(),
            ChromeUserDataDir = string.IsNullOrEmpty(local)
                ? null
                : Path.Combine(local, "Google", "Chrome", "User Data"),
            TempDir = Path.GetTempPath(),
            PersistenceKeyPresent = OperatingSystem.IsWindows() ? PersistenceKeyPresent() : null,
            DebugLogBaseline = debugLogBaseline,
        };
    }

    [SupportedOSPlatform("windows")]
    private static bool PersistenceKeyPresent()
    {
        // Only the per-user copy is the attacker's; the same CLSID under HKLM belongs to Windows.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(@"Software\Classes\CLSID\" + PersistenceClsid);
                if (key is not null)
                {
                    return true;
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                          or IOException)
            {
            }
        }
        return false;
    }
}
