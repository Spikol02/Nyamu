// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Nyakke.Core;

/// <summary>Where Nyakke keeps its own files. Nothing is written into the NIKKE folder.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
    }

    public string DataDir { get; }

    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string StateFile => Path.Combine(DataDir, "state.json");
    public string PlaytimeFile => Path.Combine(DataDir, "playtime.csv");
    public string LogFile => Path.Combine(DataDir, "nyakke.log");
    public string AgentLogFile => Path.Combine(DataDir, "agent.log");
    public string SessionDir => Path.Combine(DataDir, "session");
    public string StatusFile => Path.Combine(SessionDir, "status.json");
    public string StopFile => Path.Combine(SessionDir, "stop");
    public string EvidenceDir => Path.Combine(DataDir, "evidence");

    /// <summary>%LOCALAPPDATA%\Nyakke, or the folder named by NYAKKE_DATA_DIR.</summary>
    public static AppPaths Default()
    {
        var overrideDir = Environment.GetEnvironmentVariable("NYAKKE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return new AppPaths(overrideDir);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = AppContext.BaseDirectory;
        }
        return new AppPaths(Path.Combine(local, "Nyakke"));
    }
}
