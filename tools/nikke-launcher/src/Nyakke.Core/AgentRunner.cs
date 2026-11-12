// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace Nyakke.Core;

/// <summary>
/// One play session: opens the official launcher, waits for the game, closes the launcher and
/// watches the launcher's log folder meanwhile. It runs as its own process, with administrator
/// rights when the launcher has them, because only then may it close the launcher.
/// </summary>
public sealed class AgentRunner
{
    public const int ExitOk = 0;
    public const int ExitFailed = 2;
    public const int ExitTripwire = 3;

    private readonly AgentOptions options;
    private readonly IProcessHost host;
    private readonly Log log;
    private readonly Func<TimeSpan> clock;
    private readonly string? appDataDir;
    private readonly SessionStatus status = new();
    private string lastWritten = "";
    private TimeSpan lastWriteAt;

    public AgentRunner(AgentOptions options, IProcessHost host, Log log, Func<TimeSpan>? clock = null,
        string? appDataDir = null)
    {
        this.options = options;
        this.host = host;
        this.log = log;
        if (clock is null)
        {
            var watch = Stopwatch.StartNew();
            clock = () => watch.Elapsed;
        }
        this.clock = clock;
        this.appDataDir = appDataDir;
    }

    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(250);

    public SessionOptions? SessionOverrides { get; init; }

    public int Run(CancellationToken cancel = default)
    {
        status.AgentPid = Environment.ProcessId;
        status.StartedUtc = DateTime.UtcNow;
        FileUtil.TryDelete(options.StopFile);
        log.Write($"agent: start, root={options.InstallRoot}, close={options.CloseLauncher} after " +
                  $"{options.CloseDelaySeconds}s, resetCache={options.ResetCache}, watch={options.Watch}");

        var install = NikkeInstall.Probe(options.InstallRoot);
        if (install is null || !FileUtil.SamePath(install.Root, options.InstallRoot))
        {
            return Fail("NIKKE was not found in " + options.InstallRoot);
        }

        var alreadyOpen = host.Snapshot().Any(p => ProcessClassifier.IsLauncherSide(p, install));
        if (options.ResetCache)
        {
            ResetCache(install, alreadyOpen);
        }

        using var watch = options.Watch ? new DownloadWatch(install.LogDir, log) : null;
        watch?.Start();

        if (!host.Start(install.LauncherExe, install.LauncherDir, out var startError))
        {
            return Fail("The official launcher could not be started: " + startError);
        }
        log.Write("agent: started " + install.LauncherExe + (alreadyOpen ? " (it was already open)" : ""));

        var logic = new SessionLogic(SessionOverrides ?? new SessionOptions
        {
            Install = install,
            CloseLauncher = options.CloseLauncher,
            CloseDelay = TimeSpan.FromSeconds(options.CloseDelaySeconds),
            ClosePatterns = options.ClosePatterns,
        }, clock());

        string? lastKillError = null;
        var tick = 0;
        while (!logic.Finished)
        {
            if (cancel.IsCancellationRequested || File.Exists(options.StopFile))
            {
                logic.Finish(SessionOutcome.Stopped, "Stopped. The official launcher was left as it is.");
                break;
            }
            if (watch is not null)
            {
                watch.Poll();
                if (watch.TryTakeHit(out var hit) && hit is not null)
                {
                    HandleTripwire(hit, install);
                    logic.Finish(SessionOutcome.Tripwire,
                        "A program file appeared in the launcher's log folder. The official launcher was closed.");
                    break;
                }
            }
            if (tick++ % 2 == 0)
            {
                var step = logic.Step(clock(), host.Snapshot());
                foreach (var pid in step.CloseWindows)
                {
                    var asked = host.CloseMainWindow(pid);
                    log.Write($"agent: asked process {pid} to close its window ({(asked ? "sent" : "no window")})");
                }
                foreach (var pid in step.Kill)
                {
                    if (host.Kill(pid, out var killError))
                    {
                        log.Write($"agent: ended process {pid}");
                    }
                    else
                    {
                        lastKillError = killError;
                        log.Write($"agent: could not end process {pid}: {killError}");
                    }
                }
            }
            Publish(logic, force: false);
            if (!logic.Finished)
            {
                Thread.Sleep(Tick);
            }
        }

        if (logic.Outcome == SessionOutcome.CloseFailed && lastKillError is not null)
        {
            logic.Finish(SessionOutcome.CloseFailed,
                "The official launcher could not be closed (" + lastKillError + "). If it runs as " +
                "administrator, set \"agentElevation\" to \"Always\" in settings.json.");
        }
        log.Write($"agent: finished, outcome={logic.Outcome}: {logic.Message}");
        status.Finished = true;
        Publish(logic, force: true);
        return logic.Outcome switch
        {
            SessionOutcome.Tripwire => ExitTripwire,
            SessionOutcome.CloseFailed or SessionOutcome.LauncherNeverStarted or SessionOutcome.Failed => ExitFailed,
            _ => ExitOk,
        };
    }

    private void ResetCache(NikkeInstall install, bool launcherOpen)
    {
        var appData = appDataDir ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return;
        }
        foreach (var name in install.DataDirNames)
        {
            var dataDir = Path.Combine(appData, name);
            if (!WebCache.Exists(dataDir))
            {
                continue;
            }
            if (launcherOpen)
            {
                Note("The launcher's web cache was not moved: the launcher was already open.");
                continue;
            }
            var move = WebCache.MoveAside(dataDir, DateTime.UtcNow, options.KeepCacheBackups);
            if (move.MovedTo is not null)
            {
                Note((move.HadTraces
                         ? "The launcher's web cache held known traces and was kept as evidence in "
                         : "The launcher's web cache was moved to ") + move.MovedTo);
            }
            else if (move.Error is not null)
            {
                Note("The launcher's web cache could not be moved: " + move.Error);
            }
        }
    }

    private void HandleTripwire(TripwireHit hit, NikkeInstall install)
    {
        log.Write($"agent: TRIPWIRE {hit.Path} ({hit.Size} bytes, header {hit.HeaderHex})");
        var alert = new TripwireAlert
        {
            Path = hit.Path,
            Size = hit.Size,
            SeenUtc = hit.SeenUtc,
            HeaderHex = hit.HeaderHex,
        };
        // The downloaded program runs inside the launcher, so the launcher goes first.
        for (var round = 0; round < 3; round++)
        {
            var targets = host.Snapshot().Where(p => ProcessClassifier.IsLauncherSide(p, install)).ToList();
            if (targets.Count == 0)
            {
                break;
            }
            foreach (var target in targets)
            {
                var ended = host.Kill(target.Pid, out var error);
                log.Write($"agent: tripwire, {(ended ? "ended" : "could not end")} {target.Name} ({target.Pid}) {error}");
                if (ended && !alert.ClosedPrograms.Contains(target.Name))
                {
                    alert.ClosedPrograms.Add(target.Name);
                }
            }
            Thread.Sleep(150);
        }
        alert.EvidenceCopy = CopyEvidence(hit);
        status.Alert = alert;
    }

    private string? CopyEvidence(TripwireHit hit)
    {
        if (options.EvidenceDir is null)
        {
            return null;
        }
        try
        {
            // The launcher's downloader renames "<name>.cdt" to "<name>" when a download is done.
            var path = hit.Path;
            if (!File.Exists(path) && path.EndsWith(".cdt", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }
            Directory.CreateDirectory(options.EvidenceDir);
            var name = hit.SeenUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                       Path.GetFileName(path) + ".quarantine";
            var target = Path.Combine(options.EvidenceDir, name);
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var copy = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(copy);
            log.Write("agent: kept a copy as " + target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write("agent: could not keep a copy: " + e.Message);
            return null;
        }
    }

    private void Note(string text)
    {
        status.Notes.Add(text);
        log.Write("agent: " + text);
    }

    private int Fail(string message)
    {
        log.Write("agent: failed: " + message);
        status.Phase = SessionPhase.Finished;
        status.Outcome = SessionOutcome.Failed;
        status.Message = message;
        status.Finished = true;
        TryWriteStatus();
        return ExitFailed;
    }

    private void Publish(SessionLogic logic, bool force)
    {
        status.Phase = logic.Phase;
        status.Outcome = logic.Outcome;
        status.Message = logic.Message;
        status.GamePid = logic.GamePid;
        status.CloseCountdown = logic.CloseCountdown;
        var now = clock();
        var signature = $"{status.Phase}|{status.Outcome}|{status.Message}|{status.GamePid}|{status.Finished}";
        // Rewritten every two seconds as well, so the window can tell a live agent from a dead one.
        if (!force && signature == lastWritten && now - lastWriteAt < TimeSpan.FromSeconds(2))
        {
            return;
        }
        if (signature != lastWritten)
        {
            log.Write($"agent: {status.Phase}: {status.Message}");
        }
        lastWritten = signature;
        lastWriteAt = now;
        TryWriteStatus();
    }

    private void TryWriteStatus()
    {
        try
        {
            SessionStatus.Write(options.StatusFile, status);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Write("agent: status could not be written: " + e.Message);
        }
    }
}
