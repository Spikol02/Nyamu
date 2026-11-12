// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Nyakke.Core;

namespace Nyakke.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Command(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => run(parameter);
}

public enum Mark
{
    Unknown,
    Good,
    Bad,
    Busy,
}

/// <summary>One line of the status card.</summary>
public sealed class StatusRow(string label) : Observable
{
    private static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#6B7385"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4ADE80"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF4D5E"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#FBBF24"));
    private string value = "";
    private IBrush dot = Grey;

    public string Label { get; } = label;

    public string Value
    {
        get => value;
        private set => Set(ref this.value, value);
    }

    public IBrush Dot
    {
        get => dot;
        private set => Set(ref dot, value);
    }

    public void Show(string text, Mark mark)
    {
        Value = text;
        Dot = mark switch
        {
            Mark.Good => Green,
            Mark.Bad => Red,
            Mark.Busy => Amber,
            _ => Grey,
        };
    }
}

public enum FindingsChoice
{
    Close,
    StartAnyway,
    MarkHandled,
}

/// <summary>What the view model needs from the window.</summary>
public interface IDialogs
{
    Task<FindingsChoice> ShowFindings(string heading, string intro, IReadOnlyList<Finding> findings,
        IReadOnlyList<LinkItem> links, bool canStart, bool canMarkHandled);

    Task<bool> Confirm(string heading, string text, string yes, string no);

    Task<Settings?> EditSettings(Settings settings, AppPaths paths);

    void Minimize();

    void Restore();
}

public sealed class MainViewModel : Observable
{
    private readonly AppPaths paths;
    private readonly IProcessHost host;
    private readonly IDialogs dialogs;
    private readonly Log log;
    private readonly PlaytimeLog playtime;
    private Settings settings;
    private AppState state;
    private NikkeInstall? install;
    private DispatcherTimer? timer;
    private string title = "Nyakke";
    private string statusText = "";
    private string playText = "PLAY";
    private string playHint = "";
    private string? alertText;
    private bool canPlay;
    private bool busy;
    private Bitmap? background;
    private bool sessionActive;
    private bool gameRunning;
    private bool snapshotRunning;
    private DateTime gameStartedUtc;
    private Stopwatch? gameClock;
    private bool minimized;
    private bool alertShown;
    private List<Finding> openFindings = [];
    private ScanResult? lastScan;

    public MainViewModel(AppPaths paths, IProcessHost host, IDialogs dialogs)
    {
        this.paths = paths;
        this.host = host;
        this.dialogs = dialogs;
        log = new Log(paths.LogFile);
        playtime = new PlaytimeLog(paths.PlaytimeFile);
        settings = JsonFiles.LoadSettings(paths, out var warning);
        state = JsonFiles.LoadState(paths);
        if (warning is not null)
        {
            log.Write("settings: " + warning);
        }
        Rows = [GameRow, SafetyRow, CacheRow, ResetRow, PlaytimeRow];
        PlayCommand = new Command(_ => _ = Play());
        CheckCommand = new Command(_ => _ = ShowFindingsFromButton());
        SettingsCommand = new Command(_ => _ = EditSettings());
        FolderCommand = new Command(_ => OpenFolder(install?.Root));
        DataFolderCommand = new Command(_ => OpenFolder(paths.DataDir));
        LinkCommand = new Command(url => OpenLink(url as string));
        ApplySettings();
    }

    public StatusRow GameRow { get; } = new("Game");
    public StatusRow SafetyRow { get; } = new("Safety check");
    public StatusRow CacheRow { get; } = new("Launcher web cache");
    public StatusRow ResetRow { get; } = new("Daily reset");
    public StatusRow PlaytimeRow { get; } = new("Playtime");
    public IReadOnlyList<StatusRow> Rows { get; }
    public ObservableCollection<LinkItem> Links { get; } = [];

    public ICommand PlayCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand FolderCommand { get; }
    public ICommand DataFolderCommand { get; }
    public ICommand LinkCommand { get; }

    public string Title { get => title; private set => Set(ref title, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string PlayText { get => playText; private set => Set(ref playText, value); }
    public string PlayHint { get => playHint; private set => Set(ref playHint, value); }
    public bool CanPlay { get => canPlay; private set => Set(ref canPlay, value); }
    public Bitmap? Background { get => background; private set => Set(ref background, value); }

    public string? AlertText
    {
        get => alertText;
        private set
        {
            if (Set(ref alertText, value))
            {
                Raise(nameof(HasAlert));
            }
        }
    }

    public bool HasAlert => alertText is not null;

    public void Start()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        // A session agent from an earlier window may still be at work.
        var status = SessionStatus.TryRead(paths.StatusFile);
        sessionActive = status is { Finished: false } && DateTime.UtcNow - status.UpdatedUtc < TimeSpan.FromSeconds(10);
        Tick();
        _ = CheckNow();
    }

    public void Shutdown()
    {
        timer?.Stop();
        if (gameRunning && gameClock is not null)
        {
            playtime.Append(gameStartedUtc, gameClock.Elapsed);
        }
    }

    private void ApplySettings()
    {
        Title = settings.Title;
        Links.Clear();
        foreach (var link in settings.Links)
        {
            Links.Add(link);
        }
        install = NikkeInstall.Probe(settings.InstallDir) ?? InstallDiscovery.FindAll().FirstOrDefault();
        if (install is null)
        {
            GameRow.Show("Not found. Open Settings and choose the NIKKE folder.", Mark.Bad);
        }
        else
        {
            GameRow.Show(install.Root + "  (" + install.Flavor +
                         (install.GameExe is null ? ", game not downloaded yet" : "") + ")", Mark.Good);
        }
        Bitmap? image = null;
        if (settings.BackgroundImage is not null)
        {
            try
            {
                image = new Bitmap(settings.BackgroundImage);
            }
            catch (Exception e)
            {
                log.Write("background: " + e.Message);
            }
        }
        Background = image;
        PlayHint = settings.Mode == LaunchMode.Direct
            ? "Starts nikke.exe itself, without the official launcher (experimental)."
            : settings.CloseLauncherAfterGameStart
                ? $"Opens the official launcher and closes it {settings.CloseDelaySeconds} s after the game is up."
                : "Opens the official launcher and leaves it open.";
        UpdateCacheRow();
        UpdatePlay();
    }

    private List<string> CachesToReset()
    {
        if (install is null || settings.ResetWebCache == CacheResetMode.Never)
        {
            return [];
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return install.DataDirNames
            .Select(name => Path.Combine(appData, name))
            .Where(WebCache.Exists)
            .Where(dir => settings.ResetWebCache == CacheResetMode.Always || !state.WasCacheReset(dir))
            .ToList();
    }

    private void UpdateCacheRow()
    {
        if (settings.ResetWebCache == CacheResetMode.Never)
        {
            CacheRow.Show("Left as it is (as set)", Mark.Unknown);
        }
        else if (CachesToReset().Count > 0)
        {
            CacheRow.Show("Will be moved aside at the next start, so old scripts cannot load", Mark.Busy);
        }
        else
        {
            CacheRow.Show(settings.ResetWebCache == CacheResetMode.Always
                ? "Moved aside at every start"
                : "Fresh: nothing from before Nyakke is left in it", Mark.Good);
        }
    }

    private void UpdatePlay()
    {
        CanPlay = !busy && !sessionActive && !gameRunning && install is not null;
        PlayText = gameRunning ? "PLAYING" : sessionActive ? "STARTING" : "PLAY";
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var reset = DailyReset.ParseOrDefault(settings.DailyResetUtc);
        ResetRow.Show("in " + DurationText.Short(DailyReset.Next(now, reset) - now) +
                      "  (" + settings.DailyResetUtc + " UTC)", Mark.Unknown);
        var summary = playtime.Summarize(now, reset);
        var live = gameRunning && gameClock is not null ? gameClock.Elapsed : TimeSpan.Zero;
        PlaytimeRow.Show("today " + DurationText.Short(summary.SinceReset + live) + "  ·  total " +
                         DurationText.Short(summary.Total + live), Mark.Unknown);

        if (sessionActive)
        {
            var status = SessionStatus.TryRead(paths.StatusFile);
            if (status is not null)
            {
                StatusText = status.Message;
                if (status.Alert is not null && !alertShown)
                {
                    alertShown = true;
                    _ = ShowTripwire(status.Alert);
                }
                if (status.Finished || now - status.UpdatedUtc > TimeSpan.FromSeconds(15))
                {
                    sessionActive = false;
                    foreach (var note in status.Notes)
                    {
                        log.Write("session: " + note);
                    }
                    if (!gameRunning)
                    {
                        _ = CheckNow();
                    }
                }
            }
        }
        if (!snapshotRunning && install is not null)
        {
            snapshotRunning = true;
            var current = install;
            Task.Run(() => host.Snapshot().Any(p => ProcessClassifier.IsGame(p, current)))
                .ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    snapshotRunning = false;
                    GameState(t.IsCompletedSuccessfully && t.Result);
                }));
        }
        UpdatePlay();
    }

    private void GameState(bool running)
    {
        if (running == gameRunning)
        {
            if (running && !sessionActive && gameClock is not null)
            {
                StatusText = "The game is running  ·  " + DurationText.Clock(gameClock.Elapsed);
            }
            return;
        }
        gameRunning = running;
        if (running)
        {
            gameStartedUtc = DateTime.UtcNow;
            gameClock = Stopwatch.StartNew();
            if (settings.MinimizeWhileGameRuns && !minimized)
            {
                minimized = true;
                dialogs.Minimize();
            }
        }
        else
        {
            if (gameClock is not null)
            {
                playtime.Append(gameStartedUtc, gameClock.Elapsed);
                StatusText = "The game was closed after " + DurationText.Short(gameClock.Elapsed) + ".";
                gameClock = null;
            }
            if (minimized)
            {
                minimized = false;
                dialogs.Restore();
            }
            _ = CheckNow();
        }
        UpdatePlay();
    }

    /// <summary>Scans and returns the findings that still need attention.</summary>
    private async Task<List<Finding>> Scan()
    {
        SafetyRow.Show("Checking…", Mark.Busy);
        var request = TraceEnvironment.ForCurrentUser(install,
            install is null ? 0 : state.GetDebugLogBaseline(install.Root));
        try
        {
            lastScan = await Task.Run(() => TraceScanner.Scan(request));
        }
        catch (Exception e)
        {
            log.Write("scan: " + e);
            SafetyRow.Show("The check failed: " + e.Message, Mark.Unknown);
            return [];
        }
        openFindings = lastScan.Findings
            .Where(f => f.IsLogLine || f.IsActive || !state.IsAcknowledged(f.Key))
            .ToList();
        foreach (var finding in openFindings)
        {
            log.Write($"scan: {finding.Kind}: {finding.Path}");
        }
        if (openFindings.Count > 0)
        {
            SafetyRow.Show(openFindings.Count + " trace(s) of the November 2026 launcher compromise — " +
                           "press Safety check", Mark.Bad);
        }
        else
        {
            SafetyRow.Show("No known traces  ·  checked " + DateTime.Now.ToString("HH:mm"), Mark.Good);
        }
        return openFindings;
    }

    private async Task CheckNow()
    {
        if (busy)
        {
            return;
        }
        busy = true;
        UpdatePlay();
        try
        {
            await Scan();
        }
        finally
        {
            busy = false;
            UpdatePlay();
        }
    }

    private async Task<FindingsChoice> ShowOpenFindings(bool canStart)
    {
        const string intro =
            "These are traces the November 2026 attack on the official launcher is known to leave. " +
            "Before you open the launcher again, follow Level Infinite's manual check and keep copies " +
            "of the files named here: they are your evidence. Nyakke changes none of them.";
        var choice = await dialogs.ShowFindings("Known traces were found", intro, openFindings, settings.Links,
            canStart, canMarkHandled: openFindings.Any(f => !f.IsActive));
        if (choice == FindingsChoice.MarkHandled && lastScan is not null && install is not null)
        {
            if (lastScan.DebugLogLength >= 0)
            {
                state.SetDebugLogBaseline(install.Root, lastScan.DebugLogLength);
            }
            foreach (var finding in openFindings.Where(f => !f.IsActive && !f.IsLogLine))
            {
                state.Acknowledge(finding.Key);
            }
            JsonFiles.SaveState(paths, state);
            await Scan();
        }
        return choice;
    }

    private async Task ShowTripwire(TripwireAlert alert)
    {
        dialogs.Restore();
        AlertText = "Tripwire: a program file appeared in the launcher's log folder. The official launcher was closed.";
        log.Write("tripwire: " + alert.Path);
        var finding = new Finding(FindingKind.PayloadFile,
            "A program file appeared in the launcher's log folder while it was open",
            $"Seen at {alert.SeenUtc:HH:mm:ss} UTC, {alert.Size} bytes, starting with {alert.HeaderHex} (compiled Lua). " +
            "This is how the November 2026 agent arrived. Closed: " + string.Join(", ", alert.ClosedPrograms) + "." +
            (alert.EvidenceCopy is null ? "" : " A copy was kept in " + alert.EvidenceCopy + "."),
            alert.Path);
        await dialogs.ShowFindings("The launcher downloaded a program",
            "Treat this PC as possibly compromised: do not open the official launcher again, follow the official " +
            "manual check, and change your passwords from another device.",
            [finding], settings.Links, canStart: false, canMarkHandled: false);
    }

    private async Task Play()
    {
        if (!CanPlay || install is null)
        {
            return;
        }
        busy = true;
        UpdatePlay();
        try
        {
            if (settings.CheckBeforeStart)
            {
                if ((await Scan()).Count > 0 && await ShowOpenFindings(canStart: true) != FindingsChoice.StartAnyway)
                {
                    StatusText = "Not started.";
                    return;
                }
            }
            if (settings.Mode == LaunchMode.Direct)
            {
                StartDirect();
                return;
            }
            var caches = CachesToReset();
            if (!state.IntroShown)
            {
                var text = "Nyakke opens the official launcher, because only it can sign you in and update the game. " +
                           "Press Launch there as usual." +
                           (settings.CloseLauncherAfterGameStart
                               ? $" {settings.CloseDelaySeconds} seconds after the game is up, Nyakke closes the launcher " +
                                 "so it does not stay open in the background."
                               : "") +
                           (caches.Count > 0
                               ? " This first time, the launcher's web cache is moved aside (kept as a backup); " +
                                 "you may have to sign in again."
                               : "") +
                           " Windows may ask for administrator rights, as it does for the launcher itself.";
                if (!await dialogs.Confirm("Before the first start", text, "Start", "Cancel"))
                {
                    StatusText = "Not started.";
                    return;
                }
                state.IntroShown = true;
                JsonFiles.SaveState(paths, state);
            }
            StartSession(caches);
        }
        finally
        {
            busy = false;
            UpdatePlay();
        }
    }

    private void StartDirect()
    {
        if (install?.GameExe is null)
        {
            StatusText = "nikke.exe was not found. Let the official launcher download the game first.";
            return;
        }
        StatusText = host.Start(install.GameExe, Path.GetDirectoryName(install.GameExe)!, out var error)
            ? "Started nikke.exe."
            : "nikke.exe could not be started: " + error;
        log.Write("direct: " + StatusText);
    }

    private void StartSession(List<string> caches)
    {
        var options = new AgentOptions
        {
            InstallRoot = install!.Root,
            StatusFile = paths.StatusFile,
            StopFile = paths.StopFile,
            LogFile = paths.AgentLogFile,
            EvidenceDir = paths.EvidenceDir,
            CloseLauncher = settings.CloseLauncherAfterGameStart,
            CloseDelaySeconds = settings.CloseDelaySeconds,
            ClosePatterns = settings.CloseProcessNames,
            ResetCache = caches.Count > 0,
            KeepCacheBackups = settings.KeepCacheBackups,
            Watch = settings.WatchDownloads,
        };
        var exe = Environment.ProcessPath ?? "";
        IEnumerable<string> args = options.ToArguments();
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args = args.Prepend(Assembly.GetEntryAssembly()!.Location);
        }
        var elevate = OperatingSystem.IsWindows() &&
                      Elevation.AgentNeedsElevation(settings.AgentElevation, install.LauncherExe);
        try
        {
            Directory.CreateDirectory(paths.SessionDir);
            FileUtil.TryDelete(paths.StatusFile);
            FileUtil.TryDelete(paths.StopFile);
            using var agent = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = CommandLine.Join(args),
                UseShellExecute = OperatingSystem.IsWindows(),
                Verb = elevate ? "runas" : "",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception e)
        {
            StatusText = e.NativeErrorCode == 1223
                ? "Not started: the administrator prompt was declined."
                : "The session could not be started: " + e.Message;
            log.Write("session: " + StatusText);
            return;
        }
        foreach (var dir in caches)
        {
            state.MarkCacheReset(dir);
        }
        JsonFiles.SaveState(paths, state);
        log.Write($"session: started (elevated={elevate})");
        alertShown = false;
        AlertText = null;
        sessionActive = true;
        StatusText = "Starting the official launcher…";
        UpdateCacheRow();
    }

    private async Task EditSettings()
    {
        var edited = await dialogs.EditSettings(settings.Clone(), paths);
        if (edited is null)
        {
            return;
        }
        settings = edited;
        JsonFiles.SaveSettings(paths, settings);
        ApplySettings();
        await CheckNow();
    }

    public async Task ShowFindingsFromButton()
    {
        await CheckNow();
        if (openFindings.Count > 0)
        {
            await ShowOpenFindings(canStart: false);
        }
    }

    private void OpenFolder(string? dir)
    {
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            log.Write("open folder: " + e.Message);
        }
    }

    private void OpenLink(string? url)
    {
        if (!Settings.IsWebUrl(url))
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            log.Write("open link: " + e.Message);
        }
    }
}
