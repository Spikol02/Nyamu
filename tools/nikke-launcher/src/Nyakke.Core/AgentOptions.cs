// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace Nyakke.Core;

/// <summary>
/// What the window asks the session agent to do. Everything travels on the command line, which
/// Windows shows in the administrator prompt; the agent reads no settings file.
/// </summary>
public sealed class AgentOptions
{
    public const string Switch = "--agent";

    public required string InstallRoot { get; init; }
    public required string StatusFile { get; init; }
    public required string StopFile { get; init; }
    public string? LogFile { get; init; }
    public string? EvidenceDir { get; init; }
    public bool CloseLauncher { get; init; } = true;
    public int CloseDelaySeconds { get; init; } = 20;
    public IReadOnlyList<string> ClosePatterns { get; init; } = Settings.DefaultCloseProcessNames();
    public bool ResetCache { get; init; }
    public int KeepCacheBackups { get; init; } = 2;
    public bool Watch { get; init; } = true;

    public string[] ToArguments()
    {
        var args = new List<string>
        {
            Switch,
            "--root", InstallRoot,
            "--status", StatusFile,
            "--stop", StopFile,
            "--close", CloseLauncher ? "1" : "0",
            "--close-delay", CloseDelaySeconds.ToString(CultureInfo.InvariantCulture),
            "--reset-cache", ResetCache ? "1" : "0",
            "--keep-backups", KeepCacheBackups.ToString(CultureInfo.InvariantCulture),
            "--watch", Watch ? "1" : "0",
        };
        if (LogFile is not null)
        {
            args.AddRange(["--log", LogFile]);
        }
        if (EvidenceDir is not null)
        {
            args.AddRange(["--evidence", EvidenceDir]);
        }
        foreach (var pattern in ClosePatterns)
        {
            args.AddRange(["--close-name", pattern]);
        }
        return [.. args];
    }

    public static AgentOptions Parse(IReadOnlyList<string> args)
    {
        string? root = null, status = null, stop = null, log = null, evidence = null;
        var close = true;
        var delay = 20;
        var reset = false;
        var keep = 2;
        var watch = true;
        var patterns = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == Switch)
            {
                continue;
            }
            if (i + 1 >= args.Count)
            {
                throw new ArgumentException("Missing value for " + name);
            }
            var value = args[++i];
            switch (name)
            {
            case "--root": root = value; break;
            case "--status": status = value; break;
            case "--stop": stop = value; break;
            case "--log": log = value; break;
            case "--evidence": evidence = value; break;
            case "--close": close = value == "1"; break;
            case "--close-delay": delay = ParseInt(name, value); break;
            case "--reset-cache": reset = value == "1"; break;
            case "--keep-backups": keep = ParseInt(name, value); break;
            case "--watch": watch = value == "1"; break;
            case "--close-name": patterns.Add(value); break;
            default: throw new ArgumentException("Unknown option " + name);
            }
        }
        if (root is null || status is null || stop is null)
        {
            throw new ArgumentException("--root, --status and --stop are required");
        }
        return new AgentOptions
        {
            InstallRoot = root,
            StatusFile = status,
            StopFile = stop,
            LogFile = log,
            EvidenceDir = evidence,
            CloseLauncher = close,
            CloseDelaySeconds = Math.Clamp(delay, 0, Settings.MaxCloseDelaySeconds),
            ClosePatterns = patterns.Count > 0 ? patterns : Settings.DefaultCloseProcessNames(),
            ResetCache = reset,
            KeepCacheBackups = Math.Clamp(keep, 0, 20),
            Watch = watch,
        };
    }

    private static int ParseInt(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException("Not a number for " + name + ": " + value);
        }
        return number;
    }
}

public static class CommandLine
{
    /// <summary>Joins arguments so that Windows splits them back into the same list.</summary>
    public static string Join(IEnumerable<string> args)
    {
        var builder = new StringBuilder();
        foreach (var arg in args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            Append(builder, arg);
        }
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(arg);
            return;
        }
        builder.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself is escaped.
                builder.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }
            builder.Append(c);
            backslashes = 0;
        }
        // Backslashes before the closing quote are doubled too.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }
}
