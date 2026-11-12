// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using Nyakke.Core;
using Xunit;

namespace Nyakke.Tests;

public class FileUtilTests
{
    [Theory]
    [InlineData("nikke_launcher*.exe", "nikke_launcher.exe", true)]
    [InlineData("nikke_launcher*.exe", "NIKKE_LAUNCHER_GOOGLE.EXE", true)]
    [InlineData("nikke_launcher*.exe", "nikke.exe", false)]
    [InlineData("tbs_browser.exe", "tbs_browser.exe", true)]
    [InlineData("tbs_browser.exe", "tbs_browser.exe.bak", false)]
    [InlineData("*", "", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("*.exe", "x.exe", true)]
    [InlineData("**a*b", "xxaxxb", true)]
    [InlineData("a*b", "ab c", false)]
    public void WildcardMatch(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, FileUtil.WildcardMatch(pattern, text));
    }

    [Theory]
    [InlineData(@"C:\NIKKE\Launcher\nikke_launcher.exe", @"C:\NIKKE\Launcher", true)]
    [InlineData(@"c:\nikke\LAUNCHER\sub\x.exe", @"C:\NIKKE\Launcher\", true)]
    [InlineData(@"C:/NIKKE/Launcher/x.exe", @"C:\NIKKE\Launcher", true)]
    [InlineData(@"C:\NIKKE\Launcher", @"C:\NIKKE\Launcher", false)]
    [InlineData(@"C:\NIKKE\Launcher2\x.exe", @"C:\NIKKE\Launcher", false)]
    [InlineData(@"C:\NIKKE\NIKKE\game\nikke.exe", @"C:\NIKKE\Launcher", false)]
    public void IsUnder(string path, string dir, bool expected)
    {
        Assert.Equal(expected, FileUtil.IsUnder(path, dir));
    }

    [Fact]
    public void FindChildIgnoresCase()
    {
        using var tree = new TestTree();
        tree.File("Launcher/NIKKE_Launcher.EXE");
        var launcher = FileUtil.FindChild(tree.Root, "launcher");
        Assert.NotNull(launcher);
        Assert.NotNull(FileUtil.FindChild(launcher, "nikke_launcher.exe"));
        Assert.Null(FileUtil.FindChild(launcher, "other.exe"));
        Assert.NotNull(FileUtil.FindPath(tree.Root, "LAUNCHER", "nikke_launcher.exe"));
    }

    [Fact]
    public void AtomicWriteReplaces()
    {
        using var tree = new TestTree();
        var path = Path.Combine(tree.Root, "sub", "a.json");
        FileUtil.WriteAllTextAtomic(path, "one");
        FileUtil.WriteAllTextAtomic(path, "two");
        Assert.Equal("two", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }
}

public class CommandLineTests
{
    /// <summary>Splits a command line the way the Windows C runtime does.</summary>
    private static List<string> Split(string line)
    {
        var args = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < line.Length && line[i] is ' ' or '\t')
            {
                i++;
            }
            if (i >= line.Length)
            {
                break;
            }
            var current = new StringBuilder();
            var quoted = false;
            while (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
            {
                var slashes = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    slashes++;
                    i++;
                }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                    i++;
                }
                else
                {
                    current.Append('\\', slashes);
                    if (i < line.Length && (quoted || line[i] is not (' ' or '\t')))
                    {
                        current.Append(line[i++]);
                    }
                }
            }
            args.Add(current.ToString());
        }
        return args;
    }

    [Theory]
    [InlineData("plain")]
    [InlineData(@"C:\Program Files\NIKKE")]
    [InlineData(@"C:\Program Files\NIKKE\")]
    [InlineData(@"D:\Games\Level Infinite\NIKKE\\")]
    [InlineData("say \"hi\"")]
    [InlineData(@"back\\slash\""quote")]
    [InlineData("")]
    [InlineData("tab\there")]
    [InlineData("nikke_launcher*.exe")]
    public void JoinSurvivesWindowsSplitting(string tricky)
    {
        string[] args = ["--agent", "--root", tricky, "--status", @"C:\Users\Some One\AppData\Local\Nyakke\s.json"];
        Assert.Equal(args, Split(CommandLine.Join(args)));
    }

    [Fact]
    public void AgentOptionsRoundTrip()
    {
        var options = new AgentOptions
        {
            InstallRoot = @"D:\Games\My NIKKE\",
            StatusFile = @"C:\Users\A B\AppData\Local\Nyakke\session\status.json",
            StopFile = @"C:\Users\A B\AppData\Local\Nyakke\session\stop",
            LogFile = @"C:\x\agent.log",
            EvidenceDir = @"C:\x\evidence",
            CloseLauncher = false,
            CloseDelaySeconds = 45,
            ClosePatterns = ["nikke_launcher*.exe", "tbs_browser.exe", "intl_service.exe"],
            ResetCache = true,
            KeepCacheBackups = 5,
            Watch = false,
        };
        var parsed = AgentOptions.Parse(Split(CommandLine.Join(options.ToArguments())));
        Assert.Equal(options.InstallRoot, parsed.InstallRoot);
        Assert.Equal(options.StatusFile, parsed.StatusFile);
        Assert.Equal(options.StopFile, parsed.StopFile);
        Assert.Equal(options.LogFile, parsed.LogFile);
        Assert.Equal(options.EvidenceDir, parsed.EvidenceDir);
        Assert.False(parsed.CloseLauncher);
        Assert.Equal(45, parsed.CloseDelaySeconds);
        Assert.Equal(options.ClosePatterns, parsed.ClosePatterns);
        Assert.True(parsed.ResetCache);
        Assert.Equal(5, parsed.KeepCacheBackups);
        Assert.False(parsed.Watch);
    }

    [Fact]
    public void AgentOptionsRejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse(["--agent", "--root", "x"]));
        Assert.Throws<ArgumentException>(() => AgentOptions.Parse(["--agent", "--root"]));
        Assert.Throws<ArgumentException>(() =>
            AgentOptions.Parse(["--agent", "--root", "x", "--status", "s", "--stop", "t", "--run", "calc.exe"]));
        Assert.Throws<ArgumentException>(() =>
            AgentOptions.Parse(["--agent", "--root", "x", "--status", "s", "--stop", "t", "--close-delay", "soon"]));
    }
}

public class SettingsTests
{
    [Fact]
    public void DefaultsAreTheCautiousOnes()
    {
        var settings = new Settings();
        Assert.Equal(LaunchMode.OfficialLauncher, settings.Mode);
        Assert.True(settings.CloseLauncherAfterGameStart);
        Assert.True(settings.CheckBeforeStart);
        Assert.True(settings.WatchDownloads);
        Assert.Equal(CacheResetMode.Once, settings.ResetWebCache);
        Assert.Equal(ElevationMode.Auto, settings.AgentElevation);
        Assert.Contains("tbs_browser.exe", settings.CloseProcessNames);
    }

    [Fact]
    public void NormalizeClampsAndFilters()
    {
        var settings = new Settings
        {
            CloseDelaySeconds = 1,
            KeepCacheBackups = 999,
            Title = "  ",
            DailyResetUtc = "25:99",
            InstallDir = " ",
            CloseProcessNames = ["", " tbs_browser.exe ", "TBS_BROWSER.EXE", @"C:\Windows\explorer.exe"],
            Links =
            [
                new LinkItem { Name = "ok", Url = "https://example.com/a" },
                new LinkItem { Name = "program", Url = @"C:\Windows\System32\calc.exe" },
                new LinkItem { Name = "file", Url = "file:///C:/x.bat" },
                new LinkItem { Name = "", Url = "https://example.com/b" },
            ],
        };
        settings.Normalize();
        Assert.Equal(Settings.MinCloseDelaySeconds, settings.CloseDelaySeconds);
        Assert.Equal(20, settings.KeepCacheBackups);
        Assert.Equal("Nyakke", settings.Title);
        Assert.Equal("20:00", settings.DailyResetUtc);
        Assert.Null(settings.InstallDir);
        Assert.Equal(["tbs_browser.exe"], settings.CloseProcessNames);
        Assert.Single(settings.Links);
        Assert.Equal("ok", settings.Links[0].Name);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        using var tree = new TestTree();
        var paths = new AppPaths(tree.Root);
        var settings = new Settings
        {
            InstallDir = @"D:\Games\NIKKE",
            Mode = LaunchMode.Direct,
            CloseDelaySeconds = 33,
            ResetWebCache = CacheResetMode.Always,
            AgentElevation = ElevationMode.Never,
            Title = "Outpost",
            BackgroundImage = @"C:\art\bg.png",
            DailyResetUtc = "05:30",
        };
        JsonFiles.SaveSettings(paths, settings);
        var text = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"mode\": \"Direct\"", text);
        Assert.Contains("\"resetWebCache\": \"Always\"", text);

        var loaded = JsonFiles.LoadSettings(paths, out var warning);
        Assert.Null(warning);
        Assert.Equal(@"D:\Games\NIKKE", loaded.InstallDir);
        Assert.Equal(LaunchMode.Direct, loaded.Mode);
        Assert.Equal(33, loaded.CloseDelaySeconds);
        Assert.Equal(CacheResetMode.Always, loaded.ResetWebCache);
        Assert.Equal(ElevationMode.Never, loaded.AgentElevation);
        Assert.Equal("Outpost", loaded.Title);
        Assert.Equal("05:30", loaded.DailyResetUtc);
        Assert.Equal(3, loaded.Links.Count);
    }

    [Fact]
    public void HandEditedFileWithCommentsLoads()
    {
        using var tree = new TestTree();
        var paths = new AppPaths(tree.Root);
        File.WriteAllText(paths.SettingsFile, """
            {
              // my notes
              "closeDelaySeconds": 40,
              "CloseLauncherAfterGameStart": false,
            }
            """);
        var loaded = JsonFiles.LoadSettings(paths, out var warning);
        Assert.Null(warning);
        Assert.Equal(40, loaded.CloseDelaySeconds);
        Assert.False(loaded.CloseLauncherAfterGameStart);
        Assert.True(loaded.CheckBeforeStart);
        Assert.Equal(3, loaded.Links.Count);
    }

    [Fact]
    public void BrokenFileFallsBackAndIsKept()
    {
        using var tree = new TestTree();
        var paths = new AppPaths(tree.Root);
        File.WriteAllText(paths.SettingsFile, "{ not json");
        var loaded = JsonFiles.LoadSettings(paths, out var warning);
        Assert.NotNull(warning);
        Assert.Equal(20, loaded.CloseDelaySeconds);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void StateRemembersPerInstall()
    {
        using var tree = new TestTree();
        var paths = new AppPaths(tree.Root);
        var state = JsonFiles.LoadState(paths);
        state.SetDebugLogBaseline(@"C:\NIKKE", 1234);
        state.Acknowledge("PayloadFile|C:\\NIKKE\\LAUNCHER\\LOG\\X");
        state.MarkCacheReset(@"C:\Users\a\AppData\Roaming\nikke_launcher");
        state.IntroShown = true;
        JsonFiles.SaveState(paths, state);

        var loaded = JsonFiles.LoadState(paths);
        Assert.Equal(1234, loaded.GetDebugLogBaseline(@"c:\nikke\"));
        Assert.Equal(0, loaded.GetDebugLogBaseline(@"D:\NIKKE"));
        Assert.True(loaded.IsAcknowledged("payloadfile|c:\\nikke\\launcher\\log\\x"));
        Assert.True(loaded.WasCacheReset(@"c:\users\A\appdata\roaming\NIKKE_LAUNCHER\"));
        Assert.False(loaded.WasCacheReset(@"C:\Users\a\AppData\Roaming\nikke_launcher_hmt"));
        Assert.True(loaded.IntroShown);
    }
}

public class InstallTests
{
    [Fact]
    public void ProbeAcceptsRootLauncherExeAndGameFolder()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        Assert.Equal("PC build", install.Flavor);
        Assert.EndsWith("nikke_launcher.exe", install.LauncherExe);
        Assert.NotNull(install.GameExe);
        Assert.Equal(["nikke_launcher", "nikke_launcher_hmt"], install.DataDirNames);

        foreach (var start in new[] { install.LauncherDir, install.LauncherExe, install.GameExe!,
                     Path.GetDirectoryName(install.GameExe)!, "\"" + install.Root + "\"" })
        {
            var again = NikkeInstall.Probe(start);
            Assert.NotNull(again);
            Assert.True(FileUtil.SamePath(install.Root, again.Root), start);
        }
    }

    [Fact]
    public void ProbeHandlesMissingGameAndOtherBuilds()
    {
        using var tree = new TestTree();
        var fresh = tree.MakeInstall("Fresh", withGame: false);
        Assert.Null(fresh.GameExe);

        var google = tree.MakeInstall("NIKKE_GOOGLE", launcherExe: "nikke_launcher_google.exe");
        Assert.Equal("Google Play Games build", google.Flavor);
        Assert.Equal(["nikke_launcher_google"], google.DataDirNames);

        tree.File("Lower/launcher/Nikke_Launcher.exe");
        tree.File("Lower/data/client/NIKKE.EXE");
        var lower = NikkeInstall.Probe(Path.Combine(tree.Root, "Lower"));
        Assert.NotNull(lower);
        Assert.NotNull(lower.GameExe);
    }

    [Fact]
    public void ProbeRejectsOtherFolders()
    {
        using var tree = new TestTree();
        tree.File("Other/Launcher/setup.exe");
        tree.File("Other/Launcher/nikke.exe");
        Assert.Null(NikkeInstall.Probe(Path.Combine(tree.Root, "Other")));
        Assert.Null(NikkeInstall.Probe(Path.Combine(tree.Root, "missing")));
        Assert.Null(NikkeInstall.Probe(""));
        Assert.Null(NikkeInstall.Probe(null));
    }

    [Fact]
    public void GameInsideLauncherFolderIsNotTheGame()
    {
        using var tree = new TestTree();
        tree.File("N/Launcher/nikke_launcher.exe");
        tree.File("N/Launcher/tools/nikke.exe");
        var install = NikkeInstall.Probe(Path.Combine(tree.Root, "N"));
        Assert.NotNull(install);
        Assert.Null(install.GameExe);
    }

    [Theory]
    [InlineData("\"C:\\Games\\NIKKE\\Launcher\\nikke_launcher.exe\" \"%1\"", @"C:\Games\NIKKE")]
    [InlineData(@"D:\Games\Level Infinite\NIKKE\Launcher\nikke_launcher.exe", @"D:\Games\Level Infinite\NIKKE")]
    [InlineData("\"C:\\NIKKE_GOOGLE\\Launcher\\nikke_launcher_google.exe\" \"%1\"", @"C:\NIKKE_GOOGLE")]
    [InlineData(@"S:\games\nikke\launcher\NIKKE_LAUNCHER.EXE,0", @"S:\games\nikke")]
    [InlineData(@"C:\NIKKE", null)]
    [InlineData(@"\\server\share\NIKKE\Launcher\nikke_launcher.exe", null)]
    [InlineData("", null)]
    public void InstallDirFromCommandValue(string value, string? expected)
    {
        Assert.Equal(expected, InstallDiscovery.InstallDirFromCommandValue(value));
    }

    [Fact]
    public void ProbeAllDropsDuplicates()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        var found = InstallDiscovery.ProbeAll([install.Root, install.LauncherDir, install.LauncherExe,
            Path.Combine(tree.Root, "nothing")]);
        Assert.Single(found);
    }
}

public class ScheduleTests
{
    [Theory]
    [InlineData("20:00", true, 20, 0)]
    [InlineData("5:07", true, 5, 7)]
    [InlineData(" 00:00 ", true, 0, 0)]
    [InlineData("24:00", false, 0, 0)]
    [InlineData("12:60", false, 0, 0)]
    [InlineData("noon", false, 0, 0)]
    [InlineData("-1:00", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    public void ParseResetTime(string text, bool ok, int hours, int minutes)
    {
        Assert.Equal(ok, DailyReset.TryParse(text, out var time));
        if (ok)
        {
            Assert.Equal(new TimeSpan(hours, minutes, 0), time);
        }
    }

    [Fact]
    public void NextAndPreviousReset()
    {
        var reset = new TimeSpan(20, 0, 0);
        var before = new DateTime(2026, 11, 12, 19, 59, 59, DateTimeKind.Utc);
        var at = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(at, DailyReset.Next(before, reset));
        Assert.Equal(at.AddDays(1), DailyReset.Next(at, reset));
        Assert.Equal(at.AddDays(-1), DailyReset.Previous(before, reset));
        Assert.Equal(at, DailyReset.Previous(at, reset));
    }

    [Fact]
    public void DurationTexts()
    {
        Assert.Equal("5h 12m", DurationText.Short(new TimeSpan(5, 12, 59)));
        Assert.Equal("26h 00m", DurationText.Short(new TimeSpan(1, 2, 0, 1)));
        Assert.Equal("12m 03s", DurationText.Short(new TimeSpan(0, 12, 3)));
        Assert.Equal("45s", DurationText.Short(TimeSpan.FromSeconds(45)));
        Assert.Equal("0s", DurationText.Short(TimeSpan.FromSeconds(-5)));
        Assert.Equal("1:02:03", DurationText.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("512 B", DurationText.Bytes(512));
        Assert.Equal("1.5 MB", DurationText.Bytes(1572864));
    }

    [Fact]
    public void PlaytimeSplitsAtTheReset()
    {
        using var tree = new TestTree();
        var log = new PlaytimeLog(Path.Combine(tree.Root, "playtime.csv"));
        var reset = new TimeSpan(20, 0, 0);
        var now = new DateTime(2026, 11, 12, 22, 0, 0, DateTimeKind.Utc);
        log.Append(new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(2));
        // Started half an hour before the reset and ran one hour past it.
        log.Append(new DateTime(2026, 11, 12, 19, 30, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(90));
        log.Append(new DateTime(2026, 11, 12, 21, 30, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(20));
        log.Append(now, TimeSpan.FromMilliseconds(200));
        File.AppendAllText(Path.Combine(tree.Root, "playtime.csv"), "garbage line\n");

        var summary = log.Summarize(now, reset);
        Assert.Equal(3, summary.Sessions);
        Assert.Equal(TimeSpan.FromMinutes(120 + 90 + 20), summary.Total);
        Assert.Equal(TimeSpan.FromMinutes(60 + 20), summary.SinceReset);
    }
}

public class ElevationTests
{
    [Fact]
    public void FindsTheRequestedLevelInAManifest()
    {
        using var tree = new TestTree();
        var admin = tree.File("admin.exe", Encoding.ASCII.GetBytes(
            "MZ....<requestedExecutionLevel level=\"requireAdministrator\" uiAccess=\"false\"/>...."));
        var wide = tree.File("wide.exe", Encoding.Unicode.GetBytes("xx level=\"highestAvailable\" xx"));
        var plain = tree.File("plain.exe", Encoding.ASCII.GetBytes(
            "MZ....<requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"/>...."));
        Assert.True(Elevation.ManifestRequestsElevation(admin));
        Assert.True(Elevation.ManifestRequestsElevation(wide));
        Assert.False(Elevation.ManifestRequestsElevation(plain));
        Assert.False(Elevation.ManifestRequestsElevation(Path.Combine(tree.Root, "missing.exe")));
    }
}
