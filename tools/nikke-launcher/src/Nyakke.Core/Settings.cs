// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Nyakke.Core;

public enum LaunchMode
{
    /// <summary>Open the official launcher (it signs you in and starts the game).</summary>
    OfficialLauncher,

    /// <summary>Run nikke.exe itself. Untested: the game normally gets its sign-in from the launcher.</summary>
    Direct,
}

public enum CacheResetMode
{
    Never,
    Once,
    Always,
}

public enum ElevationMode
{
    /// <summary>Ask for administrator rights only when the official launcher itself requires them.</summary>
    Auto,
    Always,
    Never,
}

public sealed class LinkItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class Settings
{
    public const int MinCloseDelaySeconds = 5;
    public const int MaxCloseDelaySeconds = 600;

    /// <summary>NIKKE install folder (the one that holds "Launcher"). Empty: find it.</summary>
    public string? InstallDir { get; set; }

    public LaunchMode Mode { get; set; } = LaunchMode.OfficialLauncher;

    public bool CloseLauncherAfterGameStart { get; set; } = true;

    /// <summary>How long the game has to be up before the launcher is closed.</summary>
    public int CloseDelaySeconds { get; set; } = 20;

    /// <summary>Programs in the Launcher folder that are closed. Wildcards allowed.</summary>
    public List<string> CloseProcessNames { get; set; } = DefaultCloseProcessNames();

    public CacheResetMode ResetWebCache { get; set; } = CacheResetMode.Once;

    public int KeepCacheBackups { get; set; } = 2;

    public bool CheckBeforeStart { get; set; } = true;

    public bool WatchDownloads { get; set; } = true;

    public bool MinimizeWhileGameRuns { get; set; } = true;

    public ElevationMode AgentElevation { get; set; } = ElevationMode.Auto;

    public string Title { get; set; } = "Nyakke";

    public string? BackgroundImage { get; set; }

    /// <summary>Daily reset as HH:mm in UTC.</summary>
    public string DailyResetUtc { get; set; } = DailyReset.DefaultText;

    public List<LinkItem> Links { get; set; } = DefaultLinks();

    public static List<string> DefaultCloseProcessNames() => ["nikke_launcher*.exe", "tbs_browser.exe"];

    public static List<LinkItem> DefaultLinks() =>
    [
        new LinkItem
        {
            Name = "Official security notice",
            Url = "https://nikke-en.com/newsdetail.html?content_id=b4f23938a63a0a4e68aa577af395b1edb172",
        },
        new LinkItem
        {
            Name = "Official manual check",
            Url = "https://nikke-en.com/newsdetail.html?content_id=ecdccd40a55a5a46aaa9abda64d436082db7",
        },
        new LinkItem
        {
            Name = "NIKKE Launcher Check (community)",
            Url = "https://github.com/MotherSphere/NIKKE-Launcher-Check/releases/latest",
        },
    ];

    /// <summary>Brings hand-edited values back into range.</summary>
    public void Normalize()
    {
        CloseDelaySeconds = Math.Clamp(CloseDelaySeconds, MinCloseDelaySeconds, MaxCloseDelaySeconds);
        KeepCacheBackups = Math.Clamp(KeepCacheBackups, 0, 20);
        CloseProcessNames ??= DefaultCloseProcessNames();
        CloseProcessNames = CloseProcessNames
            .Select(n => n?.Trim() ?? "")
            .Where(n => n.Length > 0 && n.IndexOfAny(['\\', '/', ':']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Links ??= DefaultLinks();
        Links = Links.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name) && IsWebUrl(l.Url)).ToList();
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = "Nyakke";
        }
        if (!DailyReset.TryParse(DailyResetUtc, out _))
        {
            DailyResetUtc = DailyReset.DefaultText;
        }
        if (string.IsNullOrWhiteSpace(InstallDir))
        {
            InstallDir = null;
        }
        if (string.IsNullOrWhiteSpace(BackgroundImage))
        {
            BackgroundImage = null;
        }
    }

    /// <summary>Only web links are opened; a settings file cannot make Nyakke start a program.</summary>
    public static bool IsWebUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }

    public Settings Clone()
    {
        var copy = (Settings)MemberwiseClone();
        copy.CloseProcessNames = [.. CloseProcessNames];
        copy.Links = Links.Select(l => new LinkItem { Name = l.Name, Url = l.Url }).ToList();
        return copy;
    }
}

/// <summary>What Nyakke remembers between runs, apart from the settings.</summary>
public sealed class AppState
{
    /// <summary>debug.log length per install up to which known lines were reviewed.</summary>
    public Dictionary<string, long> DebugLogBaselines { get; set; } = [];

    public List<string> AcknowledgedFindings { get; set; } = [];

    /// <summary>Launcher data folders whose web cache was already moved aside once.</summary>
    public List<string> CacheResetDone { get; set; } = [];

    public bool IntroShown { get; set; }

    public long GetDebugLogBaseline(string installRoot)
    {
        return DebugLogBaselines.TryGetValue(FileUtil.NormalizeForCompare(installRoot), out var value) ? value : 0;
    }

    public void SetDebugLogBaseline(string installRoot, long length)
    {
        DebugLogBaselines[FileUtil.NormalizeForCompare(installRoot)] = Math.Max(0, length);
    }

    public bool IsAcknowledged(string findingKey)
    {
        return AcknowledgedFindings.Contains(findingKey, StringComparer.OrdinalIgnoreCase);
    }

    public void Acknowledge(string findingKey)
    {
        if (!IsAcknowledged(findingKey))
        {
            AcknowledgedFindings.Add(findingKey);
        }
    }

    public bool WasCacheReset(string dataDir)
    {
        var key = FileUtil.NormalizeForCompare(dataDir);
        return CacheResetDone.Any(d => FileUtil.NormalizeForCompare(d) == key);
    }

    public void MarkCacheReset(string dataDir)
    {
        if (!WasCacheReset(dataDir))
        {
            CacheResetDone.Add(dataDir);
        }
    }
}
