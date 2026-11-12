// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Nyakke.Core;
using Xunit;

namespace Nyakke.Tests;

public class AgentRunnerTests
{
    private static readonly byte[] CompiledLua = [0x1B, 0x4C, 0x4A, 0x01, 0x02, 0x00, 0x11, 0x22];

    private sealed class Rig : IDisposable
    {
        public Rig(bool close = true, bool resetCache = false, bool watch = true)
        {
            Install = Tree.MakeInstall();
            AppData = Tree.Dir("appdata");
            Paths = new AppPaths(Tree.Dir("data"));
            Host.OnStart = _ =>
            {
                Launcher = Host.Add(Install.LauncherExe);
                Browser = Host.Add(Path.Combine(Install.LauncherDir, "tbs_browser.exe"));
                Service = Host.Add(Path.Combine(Install.LauncherDir, "intl_service", "intl_service.exe"));
                return true;
            };
            Options = new AgentOptions
            {
                InstallRoot = Install.Root,
                StatusFile = Paths.StatusFile,
                StopFile = Paths.StopFile,
                LogFile = Paths.AgentLogFile,
                EvidenceDir = Paths.EvidenceDir,
                CloseLauncher = close,
                ResetCache = resetCache,
                Watch = watch,
            };
        }

        public TestTree Tree { get; } = new();
        public NikkeInstall Install { get; }
        public string AppData { get; }
        public AppPaths Paths { get; }
        public FakeProcessHost Host { get; } = new();
        public AgentOptions Options { get; set; }
        public ProcessInfo? Launcher { get; private set; }
        public ProcessInfo? Browser { get; private set; }
        public ProcessInfo? Service { get; private set; }

        public Task<int> Start()
        {
            var runner = new AgentRunner(Options, Host, new Log(Paths.AgentLogFile), appDataDir: AppData)
            {
                Tick = TimeSpan.FromMilliseconds(10),
                SessionOverrides = new SessionOptions
                {
                    Install = Install,
                    CloseLauncher = Options.CloseLauncher,
                    CloseDelay = TimeSpan.FromMilliseconds(200),
                    ClosePatterns = Options.ClosePatterns,
                    StartTimeout = TimeSpan.FromSeconds(1),
                    IdleExit = TimeSpan.FromMilliseconds(400),
                    GracefulWait = TimeSpan.FromMilliseconds(80),
                    KillWait = TimeSpan.FromMilliseconds(400),
                },
            };
            return Task.Run(() => runner.Run(), TestContext.Current.CancellationToken);
        }

        public SessionStatus Status() => SessionStatus.TryRead(Paths.StatusFile) ?? throw new InvalidOperationException("no status");

        public string LogText() => File.Exists(Paths.AgentLogFile) ? File.ReadAllText(Paths.AgentLogFile) : "";

        public void Dispose() => Tree.Dispose();
    }

    private static async Task<int> Finish(Task<int> run)
    {
        return await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("timed out waiting for " + what);
    }

    [Fact]
    public async Task OpensTheLauncherThenClosesItOnceTheGameIsUp()
    {
        using var rig = new Rig(resetCache: true);
        rig.Tree.Text("appdata/nikke_launcher/tbs_cache/Cache/data_1", "old scripts");
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        Assert.Equal([rig.Install.LauncherExe], rig.Host.Started);

        var game = rig.Host.Add(rig.Install.GameExe!);
        Assert.Equal(AgentRunner.ExitOk, await Finish(run));

        Assert.False(rig.Host.IsRunning(rig.Launcher!.Pid));
        Assert.False(rig.Host.IsRunning(rig.Browser!.Pid));
        Assert.True(rig.Host.IsRunning(rig.Service!.Pid));
        Assert.True(rig.Host.IsRunning(game.Pid));
        Assert.Contains(rig.Launcher.Pid, rig.Host.AskedToClose);
        Assert.Contains(rig.Launcher.Pid, rig.Host.Killed);

        var status = rig.Status();
        Assert.True(status.Finished);
        Assert.Equal(SessionOutcome.LauncherClosed, status.Outcome);
        Assert.Equal(SessionPhase.Finished, status.Phase);
        Assert.Null(status.Alert);
        Assert.Contains(status.Notes, n => n.Contains("was moved to"));
        Assert.False(WebCache.Exists(Path.Combine(rig.AppData, "nikke_launcher")));
        Assert.Single(WebCache.Backups(Path.Combine(rig.AppData, "nikke_launcher")));
        Assert.Contains("outcome=LauncherClosed", rig.LogText());
    }

    [Fact]
    public async Task LauncherThatClosesOnRequestIsNotKilled()
    {
        using var rig = new Rig();
        rig.Host.ClosesOnRequest = _ => true;
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        rig.Host.Add(rig.Install.GameExe!);
        Assert.Equal(AgentRunner.ExitOk, await Finish(run));
        Assert.Empty(rig.Host.Killed);
        Assert.Equal(SessionOutcome.LauncherClosed, rig.Status().Outcome);
    }

    [Fact]
    public async Task CacheIsLeftAloneWhileTheLauncherIsAlreadyOpen()
    {
        using var rig = new Rig(resetCache: true);
        rig.Tree.Text("appdata/nikke_launcher/tbs_cache/Cache/data_1", "in use");
        rig.Host.Add(rig.Install.LauncherExe);
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        rig.Host.Add(rig.Install.GameExe!);
        await Finish(run);
        Assert.True(WebCache.Exists(Path.Combine(rig.AppData, "nikke_launcher")));
        Assert.Contains(rig.Status().Notes, n => n.Contains("already open"));
    }

    [Fact]
    public async Task StopFileEndsTheSessionAndLeavesTheLauncher()
    {
        using var rig = new Rig();
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        File.WriteAllText(rig.Paths.StopFile, "");
        Assert.Equal(AgentRunner.ExitOk, await Finish(run));
        Assert.True(rig.Host.IsRunning(rig.Launcher!.Pid));
        Assert.Empty(rig.Host.Killed);
        Assert.Equal(SessionOutcome.Stopped, rig.Status().Outcome);
    }

    [Fact]
    public async Task KeepsTheLauncherWhenAskedTo()
    {
        using var rig = new Rig(close: false);
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        var game = rig.Host.Add(rig.Install.GameExe!);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        Assert.Empty(rig.Host.AskedToClose);

        // The player quits the game and then the launcher.
        rig.Host.Remove(game.Pid);
        foreach (var process in rig.Host.Snapshot())
        {
            rig.Host.Remove(process.Pid);
        }
        Assert.Equal(AgentRunner.ExitOk, await Finish(run));
        Assert.Equal(SessionOutcome.LauncherExited, rig.Status().Outcome);
        Assert.Empty(rig.Host.Killed);
    }

    [Fact]
    public async Task RefusedKillIsReportedWithTheReason()
    {
        using var rig = new Rig();
        rig.Host.AllowsKill = _ => false;
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        rig.Host.Add(rig.Install.GameExe!);
        Assert.Equal(AgentRunner.ExitFailed, await Finish(run));
        var status = rig.Status();
        Assert.Equal(SessionOutcome.CloseFailed, status.Outcome);
        Assert.Contains("Access is denied", status.Message);
        Assert.Contains("agentElevation", status.Message);
    }

    [Fact]
    public async Task FailsCleanlyWhenTheLauncherCannotStart()
    {
        using var rig = new Rig();
        rig.Host.OnStart = _ => false;
        Assert.Equal(AgentRunner.ExitFailed, await Finish(rig.Start()));
        var status = rig.Status();
        Assert.True(status.Finished);
        Assert.Equal(SessionOutcome.Failed, status.Outcome);
        Assert.Contains("could not be started", status.Message);
    }

    [Fact]
    public async Task FailsCleanlyWithoutAnInstall()
    {
        using var rig = new Rig();
        rig.Options = new AgentOptions
        {
            InstallRoot = Path.Combine(rig.Tree.Root, "nowhere"),
            StatusFile = rig.Paths.StatusFile,
            StopFile = rig.Paths.StopFile,
        };
        Assert.Equal(AgentRunner.ExitFailed, await Finish(rig.Start()));
        Assert.Empty(rig.Host.Started);
        Assert.Contains("was not found", rig.Status().Message);
    }

    [Fact]
    public async Task InstallRootMustBeTheRootItself()
    {
        using var rig = new Rig();
        // A path inside the install would be resolved upwards by the probe; the agent wants the root.
        rig.Options = new AgentOptions
        {
            InstallRoot = rig.Install.LauncherDir,
            StatusFile = rig.Paths.StatusFile,
            StopFile = rig.Paths.StopFile,
        };
        Assert.Equal(AgentRunner.ExitFailed, await Finish(rig.Start()));
        Assert.Empty(rig.Host.Started);
    }

    [Fact]
    public async Task CompiledLuaInTheLogFolderClosesTheLauncherAtOnce()
    {
        using var rig = new Rig();
        // Something that was there before the session is not this session's alarm.
        rig.Tree.File("NIKKE/Launcher/log/tinyget/old-find.log", CompiledLua);
        rig.Tree.Text("NIKKE/Launcher/log/tinyget/tinyget.20261105-023103-784.log", "plain log");
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        // The launcher's downloader writes "<name>.cdt" and renames it when the download is done.
        var partial = rig.Tree.File("NIKKE/Launcher/log/tinyget/tinyget.20261112-020000-001.log.cdt", CompiledLua);
        var done = partial[..^4];
        File.Move(partial, done);

        Assert.Equal(AgentRunner.ExitTripwire, await Finish(run));
        Assert.False(rig.Host.IsRunning(rig.Launcher!.Pid));
        Assert.False(rig.Host.IsRunning(rig.Browser!.Pid));
        Assert.False(rig.Host.IsRunning(rig.Service!.Pid));

        var status = rig.Status();
        Assert.Equal(SessionOutcome.Tripwire, status.Outcome);
        Assert.NotNull(status.Alert);
        Assert.StartsWith("1B4C4A", status.Alert.HeaderHex);
        Assert.Contains("tinyget.20261112-020000-001.log", status.Alert.Path);
        Assert.Contains("nikke_launcher.exe", status.Alert.ClosedPrograms);
        Assert.NotNull(status.Alert.EvidenceCopy);
        Assert.Equal(CompiledLua, File.ReadAllBytes(status.Alert.EvidenceCopy));
        Assert.EndsWith(".quarantine", status.Alert.EvidenceCopy);
    }

    [Fact]
    public async Task WatchCanBeTurnedOff()
    {
        using var rig = new Rig(watch: false);
        var run = rig.Start();
        await WaitFor(() => rig.Launcher is not null, "the launcher");
        rig.Tree.File("NIKKE/Launcher/log/tinyget/tinyget.x.log", CompiledLua);
        rig.Host.Add(rig.Install.GameExe!);
        Assert.Equal(AgentRunner.ExitOk, await Finish(run));
        Assert.Null(rig.Status().Alert);
    }
}

public class DownloadWatchTests
{
    private static readonly byte[] CompiledLua = [0x1B, 0x4C, 0x4A, 0x01, 0x00];

    private static async Task<TripwireHit?> PollUntilHit(DownloadWatch watch, int tries = 100)
    {
        for (var i = 0; i < tries; i++)
        {
            watch.Poll();
            if (watch.TryTakeHit(out var hit))
            {
                return hit;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return null;
    }

    [Fact]
    public async Task ReportsANewCompiledLuaFileOnce()
    {
        using var tree = new TestTree();
        var logDir = tree.Dir("log");
        tree.File("log/tinyget/before.log", CompiledLua);
        tree.Text("log/tinyget/plain.log", "text");
        using var watch = new DownloadWatch(logDir, Log.None);
        watch.Start();
        Assert.Null(await PollUntilHit(watch, tries: 10));

        var file = tree.File("log/tinyget/tinyget.new.log", CompiledLua);
        var hit = await PollUntilHit(watch);
        Assert.NotNull(hit);
        Assert.Equal(file, hit.Path);
        Assert.Equal(CompiledLua.Length, hit.Size);
        Assert.Equal("1B4C4A01", hit.HeaderHex);
        Assert.Null(await PollUntilHit(watch, tries: 10));
    }

    [Fact]
    public async Task SeesAFileThatTurnsIntoCompiledLua()
    {
        using var tree = new TestTree();
        var logDir = tree.Dir("log");
        using var watch = new DownloadWatch(logDir, Log.None);
        watch.Start();
        var file = tree.File("log/deep/er/download.tmp");
        Assert.Null(await PollUntilHit(watch, tries: 10));
        File.WriteAllBytes(file, CompiledLua);
        Assert.Equal(file, (await PollUntilHit(watch))?.Path);
    }

    [Fact]
    public async Task FolderThatAppearsLaterIsWatched()
    {
        using var tree = new TestTree();
        var logDir = Path.Combine(tree.Root, "log");
        using var watch = new DownloadWatch(logDir, Log.None);
        watch.Start();
        Assert.Null(await PollUntilHit(watch, tries: 5));
        var file = tree.File("log/tinyget/k2f.lua", CompiledLua);
        Assert.Equal(file, (await PollUntilHit(watch))?.Path);
    }

    [Fact]
    public async Task OrdinaryLogsAreNotReported()
    {
        using var tree = new TestTree();
        var logDir = tree.Dir("log");
        using var watch = new DownloadWatch(logDir, Log.None);
        watch.Start();
        tree.Text("log/tinyget/tinyget.a.log", "2026-11-12 AddTask");
        tree.File("log/app.log", [0x1B, 0x4C]);
        tree.File("log/lua-source.lua", "\u001bLua"u8.ToArray());
        Assert.Null(await PollUntilHit(watch, tries: 20));
    }
}
