// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace Nyakke.Core;

/// <summary>
/// A small text log. It records program names, paths and process ids, never command lines:
/// those of the launcher can hold sign-in tokens.
/// </summary>
public sealed class Log
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string? path;
    private readonly Lock gate = new();

    public Log(string? path)
    {
        this.path = path;
    }

    public static Log None { get; } = new(null);

    public void Write(string message)
    {
        if (path is null)
        {
            return;
        }
        var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z  " +
                   message + Environment.NewLine;
        lock (gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
