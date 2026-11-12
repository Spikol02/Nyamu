// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Nyakke.Core;
using Xunit;

namespace Nyakke.Tests;

public class SessionLogicTests
{
    private static readonly NikkeInstall Install = new(
        @"C:\NIKKE",
        @"C:\NIKKE\Launcher",
        @"C:\NIKKE\Launcher\nikke_launcher.exe",
        @"C:\NIKKE\NIKKE\game\nikke.exe",
        "PC build",
        ["nikke_launcher", "nikke_launcher_hmt"]);

    private static readonly ProcessInfo Launcher = new(10, "nikke_launcher.exe", @"C:\NIKKE\Launcher\nikke_launcher.exe");
    private static readonly ProcessInfo Browser = new(11, "tbs_browser.exe", @"C:\NIKKE\Launcher\tbs_browser.exe");
    private static readonly ProcessInfo Service = new(12, "intl_service.exe", @"C:\NIKKE\Launcher\intl_service\intl_service.exe");
    private static readonly ProcessInfo Game = new(20, "nikke.exe", @"C:\NIKKE\NIKKE\game\nikke.exe");
    private static readonly ProcessInfo Unrelated = new(30, "explorer.exe", @"C:\Windows\explorer.exe");

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static SessionLogic Make(bool close = true, IReadOnlyList<string>? patterns = null)
    {
        return new SessionLogic(new SessionOptions
        {
            Install = Install,
            CloseLauncher = close,
            CloseDelay = S(20),
            ClosePatterns = patterns ?? Settings.DefaultCloseProcessNames(),
            StartTimeout = S(120),
            IdleExit = S(45),
            GracefulWait = S(4),
            KillWait = S(6),
        }, S(0));
    }

    [Fact]
    public void HappyPathClosesTheLauncherAfterTheDelay()
    {
        var logic = Make();
        Assert.Empty(logic.Step(S(1), [Unrelated]).Kill);
        Assert.Equal(SessionPhase.Starting, logic.Phase);

        logic.Step(S(3), [Unrelated, Launcher, Browser, Service]);
        Assert.Equal(SessionPhase.LauncherRunning, logic.Phase);

        // The player presses Launch after two minutes.
        logic.Step(S(120), [Launcher, Browser, Service, Game]);
        Assert.Equal(SessionPhase.GameRunning, logic.Phase);
        Assert.Equal(20, logic.CloseCountdown);
        Assert.Equal(Game.Pid, logic.GamePid);

        var waiting = logic.Step(S(139.5), [Launcher, Browser, Service, Game]);
        Assert.Empty(waiting.CloseWindows);
        Assert.Equal(1, logic.CloseCountdown);

        var ask = logic.Step(S(140), [Launcher, Browser, Service, Game]);
        Assert.Equal([Launcher.Pid, Browser.Pid], ask.CloseWindows);
        Assert.Empty(ask.Kill);
        Assert.Equal(SessionPhase.ClosingLauncher, logic.Phase);

        Assert.Empty(logic.Step(S(142), [Launcher, Browser, Service, Game]).Kill);
        var kill = logic.Step(S(144), [Launcher, Browser, Service, Game]);
        Assert.Equal([Launcher.Pid, Browser.Pid], kill.Kill);

        // The sign-in service is left alone and does not keep the session open.
        logic.Step(S(144.5), [Service, Game]);
        Assert.True(logic.Finished);
        Assert.Equal(SessionOutcome.LauncherClosed, logic.Outcome);
    }

    [Fact]
    public void LauncherThatClosesItsWindowIsNotKilled()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher]);
        logic.Step(S(2), [Launcher, Game]);
        Assert.Equal([Launcher.Pid], logic.Step(S(22), [Launcher, Game]).CloseWindows);
        var after = logic.Step(S(23), [Game]);
        Assert.Empty(after.Kill);
        Assert.Equal(SessionOutcome.LauncherClosed, logic.Outcome);
    }

    [Fact]
    public void LauncherThatNeverShowsUp()
    {
        var logic = Make();
        logic.Step(S(119), [Unrelated]);
        Assert.False(logic.Finished);
        logic.Step(S(121), [Unrelated]);
        Assert.Equal(SessionOutcome.LauncherNeverStarted, logic.Outcome);
    }

    [Fact]
    public void LauncherRestartForAnUpdateIsWaitedOut()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher]);
        // The launcher exits, its updater replaces it and starts the new one 30 s later.
        logic.Step(S(10), []);
        logic.Step(S(39), []);
        Assert.False(logic.Finished);
        logic.Step(S(40), [Launcher with { Pid = 99 }]);
        logic.Step(S(84), [Launcher with { Pid = 99 }]);
        Assert.Equal(SessionPhase.LauncherRunning, logic.Phase);

        logic.Step(S(85), []);
        logic.Step(S(131), []);
        Assert.Equal(SessionOutcome.LauncherExited, logic.Outcome);
    }

    [Fact]
    public void GameThatClosesEarlyStartsTheWaitAgain()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher]);
        logic.Step(S(10), [Launcher, Game]);
        logic.Step(S(15), [Launcher]);
        Assert.Equal(SessionPhase.LauncherRunning, logic.Phase);

        logic.Step(S(60), [Launcher, Game with { Pid = 21 }]);
        Assert.Equal(20, logic.CloseCountdown);
        Assert.Empty(logic.Step(S(79), [Launcher, Game with { Pid = 21 }]).CloseWindows);
        Assert.Single(logic.Step(S(80), [Launcher, Game with { Pid = 21 }]).CloseWindows);
    }

    [Fact]
    public void KeepingTheLauncherOpenOnlyWatches()
    {
        var logic = Make(close: false);
        logic.Step(S(1), [Launcher, Browser]);
        logic.Step(S(5), [Launcher, Browser, Game]);
        var later = logic.Step(S(3600), [Launcher, Browser, Game]);
        Assert.Empty(later.CloseWindows);
        Assert.Empty(later.Kill);
        Assert.False(logic.Finished);

        // The game ends and the player closes the launcher by hand.
        logic.Step(S(3700), [Launcher, Browser]);
        logic.Step(S(3710), []);
        logic.Step(S(3760), []);
        Assert.Equal(SessionOutcome.LauncherExited, logic.Outcome);
    }

    [Fact]
    public void LauncherThatClosesByItselfEndsTheSession()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher]);
        logic.Step(S(5), [Launcher, Game]);
        logic.Step(S(9), [Game]);
        Assert.Equal(SessionOutcome.LauncherExited, logic.Outcome);
    }

    [Fact]
    public void LauncherThatCannotBeClosedIsReported()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher]);
        logic.Step(S(2), [Launcher, Game]);
        logic.Step(S(22), [Launcher, Game]);
        Assert.Single(logic.Step(S(26), [Launcher, Game]).Kill);
        Assert.Empty(logic.Step(S(30), [Launcher, Game]).Kill);
        logic.Step(S(33), [Launcher, Game]);
        Assert.Equal(SessionOutcome.CloseFailed, logic.Outcome);
    }

    [Fact]
    public void OnlyNamedProgramsAreClosed()
    {
        var logic = Make(patterns: ["tbs_browser.exe"]);
        logic.Step(S(1), [Launcher, Browser]);
        logic.Step(S(2), [Launcher, Browser, Game]);
        Assert.Equal([Browser.Pid], logic.Step(S(22), [Launcher, Browser, Game]).CloseWindows);
        logic.Step(S(23), [Launcher, Game]);
        Assert.Equal(SessionOutcome.LauncherClosed, logic.Outcome);
    }

    [Fact]
    public void NothingMatchingToClose()
    {
        var logic = Make();
        logic.Step(S(1), [Service]);
        logic.Step(S(2), [Service, Game]);
        var step = logic.Step(S(22), [Service, Game]);
        Assert.Empty(step.CloseWindows);
        Assert.Equal(SessionOutcome.LauncherClosed, logic.Outcome);
    }

    [Fact]
    public void ProcessesWithoutAReadablePathAreJudgedByName()
    {
        var launcher = new ProcessInfo(40, "NIKKE_Launcher.exe", null);
        var game = new ProcessInfo(41, "NIKKE.exe", null);
        Assert.True(ProcessClassifier.IsLauncherSide(launcher, Install));
        Assert.True(ProcessClassifier.IsLauncherSide(new ProcessInfo(1, "tbs_browser.exe", null), Install));
        Assert.False(ProcessClassifier.IsLauncherSide(new ProcessInfo(1, "chrome.exe", null), Install));
        Assert.True(ProcessClassifier.IsGame(game, Install));

        var logic = Make();
        logic.Step(S(1), [launcher]);
        logic.Step(S(2), [launcher, game]);
        Assert.Equal([40], logic.Step(S(22), [launcher, game]).CloseWindows);
    }

    [Fact]
    public void OtherInstallsAndLookAlikesAreIgnored()
    {
        var otherGame = new ProcessInfo(50, "nikke.exe", @"D:\Other\NIKKE\game\nikke.exe");
        var otherLauncher = new ProcessInfo(51, "nikke_launcher.exe", @"D:\Other\Launcher\nikke_launcher.exe");
        var inLauncher = new ProcessInfo(52, "nikke.exe", @"C:\NIKKE\Launcher\nikke.exe");
        Assert.False(ProcessClassifier.IsGame(otherGame, Install));
        Assert.False(ProcessClassifier.IsLauncherSide(otherLauncher, Install));
        Assert.False(ProcessClassifier.IsGame(inLauncher, Install));
        Assert.True(ProcessClassifier.IsLauncherSide(inLauncher, Install));

        var logic = Make();
        logic.Step(S(1), [otherGame, otherLauncher]);
        Assert.Equal(SessionPhase.Starting, logic.Phase);
    }

    [Fact]
    public void GameAlreadyRunningCountsFromTheFirstLook()
    {
        var logic = Make();
        logic.Step(S(1), [Launcher, Game]);
        Assert.Equal(SessionPhase.GameRunning, logic.Phase);
        Assert.Equal(20, logic.CloseCountdown);
    }

    [Fact]
    public void FinishedSessionDoesNothing()
    {
        var logic = Make();
        logic.Finish(SessionOutcome.Stopped, "stopped");
        var step = logic.Step(S(500), [Launcher, Game]);
        Assert.Empty(step.CloseWindows);
        Assert.Empty(step.Kill);
        Assert.Equal(SessionOutcome.Stopped, logic.Outcome);
    }
}
