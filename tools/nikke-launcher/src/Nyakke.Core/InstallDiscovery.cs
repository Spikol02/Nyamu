// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Nyakke.Core;

/// <summary>Finds NIKKE installs from what the official launcher records and from the usual folders.</summary>
public static class InstallDiscovery
{
    private static readonly string[] UsualFolders =
    [
        "NIKKE",
        "NIKKE_GOOGLE",
        @"Program Files\NIKKE",
        @"Program Files (x86)\NIKKE",
        @"Games\NIKKE",
        @"Game\NIKKE",
        @"Level Infinite\NIKKE",
        @"Games\Level Infinite\NIKKE",
    ];

    public static List<NikkeInstall> FindAll(IEnumerable<string>? extraCandidates = null)
    {
        var candidates = new List<string>();
        if (extraCandidates is not null)
        {
            candidates.AddRange(extraCandidates);
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                candidates.AddRange(RegistryCandidates());
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                          or IOException)
            {
            }
            candidates.AddRange(DriveCandidates());
        }
        return ProbeAll(candidates);
    }

    public static List<NikkeInstall> ProbeAll(IEnumerable<string> candidates)
    {
        var found = new List<NikkeInstall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var install = NikkeInstall.Probe(candidate);
            if (install is not null && seen.Add(FileUtil.NormalizeForCompare(install.Root)))
            {
                found.Add(install);
            }
        }
        return found;
    }

    /// <summary>
    /// <c>"C:\Games\NIKKE\Launcher\nikke_launcher.exe" "%1"</c> gives <c>C:\Games\NIKKE</c>.
    /// </summary>
    public static string? InstallDirFromCommandValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        foreach (var exe in NikkeInstall.LauncherExeNames)
        {
            var end = value.IndexOf(@"\launcher\" + exe, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                continue;
            }
            var start = value.LastIndexOf(@":\", end, StringComparison.Ordinal);
            if (start < 1 || !char.IsAsciiLetter(value[start - 1]))
            {
                continue;
            }
            return value[(start - 1)..end];
        }
        return null;
    }

    private static IEnumerable<string> DriveCandidates()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var drive in drives)
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }
            foreach (var folder in UsualFolders)
            {
                yield return Path.Combine(drive.Name, folder);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> RegistryCandidates()
    {
        var result = new List<string>();

        void AddValue(RegistryHive hive, RegistryView view, string subKey, string? valueName)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string text && text.Length > 0)
                {
                    result.Add(InstallDirFromCommandValue(text) ?? text);
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                          or IOException)
            {
            }
        }

        // The launcher registers a URL protocol and an uninstall entry that name its folder.
        AddValue(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Classes\nikkelauncher\DefaultIcon", null);
        AddValue(RegistryHive.CurrentUser, RegistryView.Default,
            @"Software\Classes\nikkelauncher\shell\open\command", null);

        (RegistryHive Hive, RegistryView View)[] places =
        [
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        ];
        foreach (var (hive, view) in places)
        {
            const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\nikke_launcher";
            AddValue(hive, view, uninstall, "GameInstallPath");
            AddValue(hive, view, uninstall, "InstallLocation");
            AddValue(hive, view, uninstall, "DisplayIcon");
            foreach (var exe in NikkeInstall.LauncherExeNames)
            {
                var appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe;
                AddValue(hive, view, appPath, null);
                AddValue(hive, view, appPath, "InstallPath");
            }
        }
        return result;
    }
}
