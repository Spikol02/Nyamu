// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Nyakke.Core;
using Xunit;

namespace Nyakke.Tests;

public class TraceScannerTests
{
    private const string CleanLine = "[1105/023103.001:INFO:CONSOLE(1)] \"[js-bridge] page loaded\", source: https://x/y.js (1)\n";
    private const string AgentLines =
        "[1103/004736.501:INFO:CONSOLE(0)] \"lua downloaded to log\\tinyget\\tinyget.20261103-004735-112.log\", source:  (0)\n" +
        "[1103/004736.612:INFO:CONSOLE(0)] \"k2f_chan registered\", source:  (0)\n" +
        "[1103/004737.020:INFO:CONSOLE(0)] \"adopted 4c1f\", source:  (0)\n";

    private static ScanResult Scan(NikkeInstall install, long baseline = 0, string? appData = null,
        string? chrome = null, string? temp = null, bool? key = false)
    {
        return TraceScanner.Scan(new ScanRequest
        {
            Install = install,
            LauncherDataDirs = appData is null ? [] : TraceEnvironment.LauncherDataDirs(appData),
            ChromeUserDataDir = chrome,
            TempDir = temp,
            PersistenceKeyPresent = key,
            DebugLogBaseline = baseline,
        });
    }

    [Fact]
    public void CleanInstallHasNoFindings()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.Text("NIKKE/Launcher/intl_service/debug.log", string.Concat(Enumerable.Repeat(CleanLine, 50)));
        tree.Text("NIKKE/Launcher/log/tinyget/tinyget.20261105-023103-784.log", "2026-11-05 AddTask url=https://x\n");
        tree.Text("NIKKE/Launcher/log/app.nikke_launcher.20261105.log", "\u0001\u0002encrypted");
        tree.Dir("appdata", "nikke_launcher", "tbs_cache", "Code Cache", "js");
        tree.File("appdata/nikke_launcher/tbs_cache/Code Cache/js/0123456789abcdef_0", [1, 2, 3]);
        tree.Text("appdata/nikke_launcher/tbs_cache/Local Storage/leveldb/000003.log", "launcher.lang=en");
        tree.Dir("chrome", "PlatformExperienceHelper");
        tree.File("chrome/PlatformExperienceHelper/LoadFeatures");
        tree.Dir("temp");

        var result = Scan(install, appData: Path.Combine(tree.Root, "appdata"),
            chrome: Path.Combine(tree.Root, "chrome"), temp: Path.Combine(tree.Root, "temp"));
        Assert.Empty(result.Findings);
        Assert.True(result.DebugLogLength > 0);
        Assert.NotEmpty(result.Checked);
    }

    [Fact]
    public void InstallWithoutLogsIsFine()
    {
        using var tree = new TestTree();
        var result = Scan(tree.MakeInstall());
        Assert.Empty(result.Findings);
        Assert.Equal(-1, result.DebugLogLength);
    }

    [Fact]
    public void AgentLinesInDebugLogAreFound()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.Text("NIKKE/Launcher/intl_service/debug.log", CleanLine + AgentLines + CleanLine);
        var result = Scan(install);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(FindingKind.AgentLog, finding.Kind);
        Assert.True(finding.IsLogLine);
        Assert.False(finding.IsActive);
        Assert.Contains("1 line(s)", finding.Detail);
    }

    [Fact]
    public void WideCharacterLogIsRead()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.File("NIKKE/Launcher/intl_service/debug.log", Encoding.Unicode.GetBytes(CleanLine + AgentLines));
        Assert.Equal(FindingKind.AgentLog, Assert.Single(Scan(install).Findings).Kind);
    }

    [Fact]
    public void LoaderWithoutAgentIsAWeakerFinding()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.Text("NIKKE/Launcher/intl_service/debug.log",
            CleanLine + "[1101/1:ERROR] \"Failed to load\", source: https://insightjs.com/l.js?f=aegis (1)\n");
        Assert.Equal(FindingKind.LoaderLog, Assert.Single(Scan(install).Findings).Kind);
    }

    [Fact]
    public void ReviewedLinesAreNotReportedAgainButNewOnesAre()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        var log = tree.Text("NIKKE/Launcher/intl_service/debug.log", CleanLine + AgentLines);
        var first = Scan(install);
        Assert.Single(first.Findings);

        var reviewed = Scan(install, baseline: first.DebugLogLength);
        Assert.Empty(reviewed.Findings);

        File.AppendAllText(log, CleanLine + CleanLine);
        Assert.Empty(Scan(install, baseline: first.DebugLogLength).Findings);

        File.AppendAllText(log, AgentLines);
        Assert.Single(Scan(install, baseline: first.DebugLogLength).Findings);

        // A log shorter than the reviewed length is a new log and is read from the start.
        File.WriteAllText(log, AgentLines);
        Assert.Single(Scan(install, baseline: first.DebugLogLength + 100000).Findings);
    }

    [Fact]
    public void MarkersAcrossReadBoundariesAreCountedOnce()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        // The scanner reads 256 KiB at a time: put markers on and around the boundaries.
        var bytes = new byte[3 * (1 << 18) + 4096];
        Array.Fill(bytes, (byte)'.');
        var marker = Encoding.ASCII.GetBytes("k2f_chan");
        int[] positions = [0, (1 << 18) - 4, (1 << 18) + 8, 2 * (1 << 18) - 8, 2 * (1 << 18) + 1, bytes.Length - 8];
        foreach (var position in positions)
        {
            marker.CopyTo(bytes, position);
        }
        tree.File("NIKKE/Launcher/intl_service/debug.log", bytes);
        var finding = Assert.Single(Scan(install).Findings);
        Assert.Contains($"{positions.Length} line(s)", finding.Detail);
        Assert.Contains("first at byte 0", finding.Detail);
    }

    [Fact]
    public void CompiledLuaInTheLogFolderIsFound()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        var payload = tree.File("NIKKE/Launcher/log/tinyget/tinyget.20261103-004735-112.log",
            [0x1B, 0x4C, 0x4A, 0x01, 0x02, 0x00]);
        var early = tree.Text("NIKKE/Launcher/log/tinyget/k2f.lua", "anything");
        tree.Text("NIKKE/Launcher/log/tinyget/tinyget.20261105-023103-784.log", "plain log");
        tree.File("NIKKE/Launcher/log/empty.log");
        tree.File("NIKKE/Launcher/log/short.log", [0x1B, 0x4C]);

        var result = Scan(install);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(FindingKind.PayloadFile, f.Kind));
        Assert.Contains(result.Findings, f => f.Path == payload);
        Assert.Contains(result.Findings, f => f.Path == early);
    }

    [Fact]
    public void PersistenceIsActive()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        var hex = new string('a', 60) + "0f9e";
        tree.File($"chrome/PlatformExperienceHelper/{hex}/platform_experience_helper.dll", [1]);
        // Chrome's own files and look-alikes that are not the attacker's.
        tree.File("chrome/PlatformExperienceHelper/platform_experience_helper.exe", [1]);
        tree.File($"chrome/PlatformExperienceHelper/{new string('z', 64)}/platform_experience_helper.dll", [1]);
        tree.File($"chrome/PlatformExperienceHelper/{hex[..63]}/platform_experience_helper.dll", [1]);
        tree.File($"chrome/PlatformExperienceHelper/{new string('b', 64)}/other.dll", [1]);

        var result = Scan(install, chrome: Path.Combine(tree.Root, "chrome"), key: true);
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.True(f.IsActive));
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.PersistenceRegistry);
        Assert.Contains(result.Findings, f => f.Kind == FindingKind.PersistenceDll && f.Path!.Contains(hex));
    }

    [Fact]
    public void RegistryNotCheckedIsNotAFinding()
    {
        using var tree = new TestTree();
        Assert.Empty(Scan(tree.MakeInstall(), key: null).Findings);
    }

    [Fact]
    public void WebCacheTracesAreFoundInEveryDataFolder()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.File("appdata/nikke_launcher/tbs_cache/Code Cache/js/dd65fa4d61079e7e_0", [1]);
        tree.File("appdata/nikke_launcher/tbs_cache/Code Cache/js/fd520c7c0f65c2d7_0", [1]);
        tree.Text("appdata/nikke_launcher_google/tbs_cache/Local Storage/leveldb/000005.log",
            "\u0001_https://playerinfinite.com\u0000\u0001k2f.agent.state{\"id\":\"x\"}");
        tree.Dir("appdata", "nikke_launcher_hmt");
        tree.Dir("appdata", "unrelated", "tbs_cache", "Code Cache", "js");
        tree.File("appdata/unrelated/tbs_cache/Code Cache/js/dd65fa4d61079e7e_0", [1]);

        var result = Scan(install, appData: Path.Combine(tree.Root, "appdata"));
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(2, result.Findings.Count(f => f.Kind == FindingKind.CacheLoader));
        Assert.Single(result.Findings, f => f.Kind == FindingKind.CacheAgentState);
        Assert.True(TraceScanner.CacheHasKnownTraces(Path.Combine(tree.Root, "appdata", "nikke_launcher", "tbs_cache")));
        Assert.False(TraceScanner.CacheHasKnownTraces(Path.Combine(tree.Root, "appdata", "nikke_launcher_hmt", "tbs_cache")));
    }

    [Fact]
    public void TempLeftoversAreFound()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        tree.File("temp/k2f-bcreds-1762130855-ab12.db", [1]);
        tree.File("temp/k2f_screen_1762130855_0.jpg", [1]);
        tree.File("temp/k2f-notes.txt", [1]);
        tree.File("temp/onominan.tmp", [1]);
        var result = Scan(install, temp: Path.Combine(tree.Root, "temp"));
        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(FindingKind.TempLeftover, f.Kind));
    }

    [Fact]
    public void FindingKeysTellFilesApart()
    {
        var a = new Finding(FindingKind.PayloadFile, "t", "d", @"C:\NIKKE\Launcher\log\tinyget\a.log");
        var same = new Finding(FindingKind.PayloadFile, "x", "y", @"c:\nikke\launcher\LOG\tinyget\A.LOG");
        var other = new Finding(FindingKind.PayloadFile, "t", "d", @"C:\NIKKE\Launcher\log\tinyget\b.log");
        Assert.Equal(a.Key, same.Key);
        Assert.NotEqual(a.Key, other.Key);
    }

    [Theory]
    [InlineData(new byte[] { 0x1B, 0x4C, 0x4A, 0x01 }, true)]
    [InlineData(new byte[] { 0x1B, 0x4C, 0x4A, 0x02 }, true)]
    [InlineData(new byte[] { 0x1B, 0x4C, 0x4A }, true)]
    [InlineData(new byte[] { 0x1B, 0x4C, 0x75, 0x61 }, false)]
    [InlineData(new byte[] { 0x1B, 0x4C }, false)]
    [InlineData(new byte[] { }, false)]
    public void LuaJitHeader(byte[] header, bool expected)
    {
        Assert.Equal(expected, TraceScanner.IsLuaJitBytecode(header));
    }
}

public class WebCacheTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 1, 30, 45, DateTimeKind.Utc);

    [Fact]
    public void MovesTheCacheAsideAndKeepsItsContent()
    {
        using var tree = new TestTree();
        var data = tree.Dir("nikke_launcher");
        tree.Text("nikke_launcher/tbs_cache/Cache/data_1", "cached");
        tree.Text("nikke_launcher/psd.dat", "login");

        var move = WebCache.MoveAside(data, Now, keepBackups: 2);
        Assert.Null(move.Error);
        Assert.False(move.HadTraces);
        Assert.Equal(Path.Combine(data, "tbs_cache.nyakke-20261112-013045"), move.MovedTo);
        Assert.False(WebCache.Exists(data));
        Assert.Equal("cached", File.ReadAllText(Path.Combine(move.MovedTo!, "Cache", "data_1")));
        Assert.True(File.Exists(Path.Combine(data, "psd.dat")));
    }

    [Fact]
    public void NothingToMove()
    {
        using var tree = new TestTree();
        var move = WebCache.MoveAside(tree.Dir("nikke_launcher"), Now, 2);
        Assert.Null(move.MovedTo);
        Assert.Null(move.Error);
        Assert.Null(WebCache.MoveAside(Path.Combine(tree.Root, "missing"), Now, 2).MovedTo);
    }

    [Fact]
    public void OldBackupsArePrunedButEvidenceIsKept()
    {
        using var tree = new TestTree();
        var data = tree.Dir("nikke_launcher");
        tree.File("nikke_launcher/tbs_cache/Code Cache/js/ce2fd3f1007a9fe9_0", [1]);
        var evidence = WebCache.MoveAside(data, Now, keepBackups: 1);
        Assert.True(evidence.HadTraces);
        Assert.Contains("tbs_cache.evidence-", evidence.MovedTo);

        for (var day = 1; day <= 4; day++)
        {
            tree.Text("nikke_launcher/tbs_cache/Cache/data_1", "day" + day);
            Assert.Null(WebCache.MoveAside(data, Now.AddDays(day), keepBackups: 1).Error);
        }
        var backups = WebCache.Backups(data);
        Assert.Single(backups);
        Assert.EndsWith("tbs_cache.nyakke-20261116-013045", backups[0]);
        Assert.Equal("day4", File.ReadAllText(Path.Combine(backups[0], "Cache", "data_1")));
        Assert.True(Directory.Exists(evidence.MovedTo));
    }

    [Fact]
    public void SameSecondGetsItsOwnFolder()
    {
        using var tree = new TestTree();
        var data = tree.Dir("nikke_launcher");
        tree.Text("nikke_launcher/tbs_cache/a", "1");
        var first = WebCache.MoveAside(data, Now, 5);
        tree.Text("nikke_launcher/tbs_cache/a", "2");
        var second = WebCache.MoveAside(data, Now, 5);
        Assert.NotEqual(first.MovedTo, second.MovedTo);
        Assert.Equal(2, WebCache.Backups(data).Count);
    }
}
