// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Nyakke.Core;

public static class Elevation
{
    private const long MaxExeBytes = 256L * 1024 * 1024;

    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>
    /// True when a program's manifest asks Windows for administrator rights. The manifest is
    /// plain XML inside the file, so it is enough to look for the request level.
    /// </summary>
    public static bool ManifestRequestsElevation(string exePath)
    {
        try
        {
            var info = new FileInfo(exePath);
            if (!info.Exists || info.Length > MaxExeBytes)
            {
                return false;
            }
            var bytes = File.ReadAllBytes(exePath);
            foreach (var level in new[] { "requireAdministrator", "highestAvailable" })
            {
                if (bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(level)) >= 0 ||
                    bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(level)) >= 0)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    /// <summary>Whether the session agent has to be started with administrator rights.</summary>
    public static bool AgentNeedsElevation(ElevationMode mode, string launcherExe)
    {
        if (IsElevated)
        {
            return false; // the agent inherits them
        }
        return mode switch
        {
            ElevationMode.Always => true,
            ElevationMode.Never => false,
            _ => ManifestRequestsElevation(launcherExe),
        };
    }
}
