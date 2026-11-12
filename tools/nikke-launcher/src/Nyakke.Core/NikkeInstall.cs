// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Nyakke.Core;

/// <summary>
/// A NIKKE PC install: <c>Root\Launcher\nikke_launcher.exe</c> and, once the game has been
/// downloaded, <c>Root\NIKKE\game\nikke.exe</c>.
/// </summary>
public sealed record NikkeInstall(
    string Root,
    string LauncherDir,
    string LauncherExe,
    string? GameExe,
    string Flavor,
    IReadOnlyList<string> DataDirNames)
{
    public const string GameExeName = "nikke.exe";

    /// <summary>Launcher programs in Root\Launcher (the Google Play Games build has its own).</summary>
    public static readonly string[] LauncherExeNames = ["nikke_launcher.exe", "nikke_launcher_google.exe"];

    /// <summary>Folders under %APPDATA% in which the launchers keep their data.</summary>
    public static readonly string[] AllDataDirNames =
        ["nikke_launcher", "nikke_launcher_hmt", "nikke_launcher_google"];

    public string LogDir => Path.Combine(LauncherDir, "log");

    public string? DebugLog => FileUtil.FindPath(LauncherDir, "intl_service", "debug.log");

    /// <summary>
    /// Accepts the install folder, its Launcher folder, the game folder or any file in them.
    /// </summary>
    public static NikkeInstall? Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        string? dir;
        try
        {
            dir = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (File.Exists(dir))
        {
            dir = Path.GetDirectoryName(dir);
        }
        for (var depth = 0; depth < 5 && !string.IsNullOrEmpty(dir); depth++)
        {
            var install = ProbeRoot(dir);
            if (install is not null)
            {
                return install;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static NikkeInstall? ProbeRoot(string root)
    {
        var launcherDir = FileUtil.FindChild(root, "Launcher");
        if (launcherDir is null || !Directory.Exists(launcherDir))
        {
            return null;
        }
        foreach (var name in LauncherExeNames)
        {
            var exe = FileUtil.FindChild(launcherDir, name);
            if (exe is null || !File.Exists(exe))
            {
                continue;
            }
            var google = name.Contains("google", StringComparison.OrdinalIgnoreCase);
            return new NikkeInstall(
                root,
                launcherDir,
                exe,
                FindGameExe(root, launcherDir),
                google ? "Google Play Games build" : "PC build",
                google ? ["nikke_launcher_google"] : ["nikke_launcher", "nikke_launcher_hmt"]);
        }
        return null;
    }

    private static string? FindGameExe(string root, string launcherDir)
    {
        string[][] usual = [["NIKKE", "game", GameExeName], ["game", GameExeName], ["NIKKE", GameExeName]];
        foreach (var parts in usual)
        {
            var found = FileUtil.FindPath(root, parts);
            if (found is not null && File.Exists(found))
            {
                return found;
            }
        }

        // A layout this code does not know: look a few levels down, never into the launcher.
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited++ < 300)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (string.Equals(Path.GetFileName(file), GameExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
                if (depth >= 3)
                {
                    continue;
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!FileUtil.SamePath(sub, launcherDir))
                    {
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
