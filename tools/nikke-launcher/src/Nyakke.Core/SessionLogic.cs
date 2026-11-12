// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Nyakke.Core;

public sealed record ProcessInfo(int Pid, string Name, string? Path);

public static class ProcessClassifier
{
    /// <summary>Used when a process path cannot be read.</summary>
    private static readonly string[] LauncherNames =
        ["nikke_launcher*.exe", "tbs_browser.exe", "intl_service.exe", "intlwebviewhelper.exe"];

    /// <summary>Anything that runs from the install's Launcher folder.</summary>
    public static bool IsLauncherSide(ProcessInfo process, NikkeInstall install)
    {
        if (process.Path is not null)
        {
            return FileUtil.IsUnder(process.Path, install.LauncherDir);
        }
        return LauncherNames.Any(pattern => FileUtil.WildcardMatch(pattern, process.Name));
    }

    public static bool IsGame(ProcessInfo process, NikkeInstall install)
    {
        if (!string.Equals(process.Name, NikkeInstall.GameExeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return process.Path is null ||
               (FileUtil.IsUnder(process.Path, install.Root) && !FileUtil.IsUnder(process.Path, install.LauncherDir));
    }

    public static bool MatchesAny(ProcessInfo process, IReadOnlyList<string> patterns)
    {
        return patterns.Any(pattern => FileUtil.WildcardMatch(pattern, process.Name));
    }
}

public enum SessionPhase
{
    Starting,
    LauncherRunning,
    GameRunning,
    ClosingLauncher,
    Finished,
}

public enum SessionOutcome
{
    None,
    LauncherClosed,
    LauncherExited,
    LauncherNeverStarted,
    CloseFailed,
    Stopped,
    Tripwire,
    Failed,
}

public sealed class SessionOptions
{
    public required NikkeInstall Install { get; init; }
    public bool CloseLauncher { get; init; } = true;
    public TimeSpan CloseDelay { get; init; } = TimeSpan.FromSeconds(20);
    public IReadOnlyList<string> ClosePatterns { get; init; } = Settings.DefaultCloseProcessNames();

    /// <summary>How long to wait for the launcher to show up after it was started.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The launcher can restart itself to update. Only this long without any launcher or game
    /// process counts as "the launcher was closed".
    /// </summary>
    public TimeSpan IdleExit { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Time the launcher gets to close its window before it is ended.</summary>
    public TimeSpan GracefulWait { get; init; } = TimeSpan.FromSeconds(4);

    public TimeSpan KillWait { get; init; } = TimeSpan.FromSeconds(6);
}

public sealed record SessionStep(IReadOnlyList<int> CloseWindows, IReadOnlyList<int> Kill)
{
    public static readonly SessionStep Nothing = new([], []);
}

/// <summary>
/// Decides what to do from the list of running processes. It has no side effects, so the whole
/// start-wait-close sequence can be tested without a launcher.
/// </summary>
public sealed class SessionLogic
{
    private readonly SessionOptions options;
    private readonly TimeSpan startedAt;
    private TimeSpan lastActivity;
    private TimeSpan gameSeenAt;
    private TimeSpan closeAskedAt;
    private TimeSpan killedAt;
    private bool killed;

    public SessionLogic(SessionOptions options, TimeSpan now)
    {
        this.options = options;
        startedAt = now;
        lastActivity = now;
    }

    public SessionPhase Phase { get; private set; } = SessionPhase.Starting;
    public SessionOutcome Outcome { get; private set; } = SessionOutcome.None;
    public string Message { get; private set; } = "Starting the official launcher";
    public int? GamePid { get; private set; }
    public bool Finished => Phase == SessionPhase.Finished;

    /// <summary>Seconds until the launcher is closed, while the game is being given time to sign in.</summary>
    public int? CloseCountdown { get; private set; }

    public void Finish(SessionOutcome outcome, string message)
    {
        Phase = SessionPhase.Finished;
        Outcome = outcome;
        Message = message;
        CloseCountdown = null;
    }

    public SessionStep Step(TimeSpan now, IReadOnlyList<ProcessInfo> processes)
    {
        if (Finished)
        {
            return SessionStep.Nothing;
        }
        var install = options.Install;
        var launcher = processes.Where(p => ProcessClassifier.IsLauncherSide(p, install)).ToList();
        var game = processes.FirstOrDefault(p => ProcessClassifier.IsGame(p, install));
        GamePid = game?.Pid;
        CloseCountdown = null;

        switch (Phase)
        {
        case SessionPhase.Starting:
            if (launcher.Count > 0 || game is not null)
            {
                Phase = SessionPhase.LauncherRunning;
                lastActivity = now;
                Message = "The official launcher is open. Press Launch there to start the game.";
                return Step(now, processes);
            }
            if (now - startedAt > options.StartTimeout)
            {
                Finish(SessionOutcome.LauncherNeverStarted, "The official launcher did not start.");
            }
            break;

        case SessionPhase.LauncherRunning:
            if (game is not null)
            {
                Phase = SessionPhase.GameRunning;
                gameSeenAt = now;
                return Step(now, processes);
            }
            if (launcher.Count > 0)
            {
                lastActivity = now;
            }
            else if (now - lastActivity > options.IdleExit)
            {
                Finish(SessionOutcome.LauncherExited, "The official launcher was closed before the game started.");
            }
            break;

        case SessionPhase.GameRunning:
            if (game is null)
            {
                // Closed or restarted before it had time to sign in: wait for it again.
                Phase = SessionPhase.LauncherRunning;
                lastActivity = now;
                Message = "The game closed. The official launcher is still open.";
                break;
            }
            if (launcher.Count == 0)
            {
                Finish(SessionOutcome.LauncherExited, "The game is running. The official launcher closed by itself.");
                break;
            }
            if (!options.CloseLauncher)
            {
                Message = "The game is running. The official launcher stays open (as set).";
                break;
            }
            var left = options.CloseDelay - (now - gameSeenAt);
            if (left > TimeSpan.Zero)
            {
                CloseCountdown = (int)Math.Ceiling(left.TotalSeconds);
                Message = $"The game is starting. Closing the official launcher in {CloseCountdown} s.";
                break;
            }
            var targets = Targets(launcher);
            if (targets.Count == 0)
            {
                Finish(SessionOutcome.LauncherClosed, "The game is running. Nothing of the launcher is left to close.");
                break;
            }
            Phase = SessionPhase.ClosingLauncher;
            closeAskedAt = now;
            Message = "Closing the official launcher.";
            return new SessionStep(targets, []);

        case SessionPhase.ClosingLauncher:
            var remaining = Targets(launcher);
            if (remaining.Count == 0)
            {
                Finish(SessionOutcome.LauncherClosed, game is not null
                    ? "The game is running. The official launcher was closed."
                    : "The official launcher was closed.");
                break;
            }
            if (!killed && now - closeAskedAt >= options.GracefulWait)
            {
                killed = true;
                killedAt = now;
                return new SessionStep([], remaining);
            }
            if (killed && now - killedAt > options.KillWait)
            {
                Finish(SessionOutcome.CloseFailed,
                    "The official launcher could not be closed. Close it from its tray icon.");
            }
            break;
        }
        return SessionStep.Nothing;
    }

    private List<int> Targets(List<ProcessInfo> launcherSide)
    {
        return launcherSide
            .Where(p => ProcessClassifier.MatchesAny(p, options.ClosePatterns))
            .Select(p => p.Pid)
            .ToList();
    }
}
