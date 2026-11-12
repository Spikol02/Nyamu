// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Nyakke.Core;

public sealed record PlaytimeSummary(TimeSpan SinceReset, TimeSpan Total, int Sessions);

/// <summary>One line per play session: start time in UTC and length in seconds.</summary>
public sealed class PlaytimeLog
{
    private readonly string path;

    public PlaytimeLog(string path)
    {
        this.path = path;
    }

    public void Append(DateTime startUtc, TimeSpan length)
    {
        if (length < TimeSpan.FromSeconds(1))
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var line = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) + "," +
                       ((long)length.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "\n";
            File.AppendAllText(path, line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IEnumerable<(DateTime StartUtc, TimeSpan Length)> Read()
    {
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                yield break;
            }
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length == 2 &&
                DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start) &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                yield return (start, TimeSpan.FromSeconds(seconds));
            }
        }
    }

    /// <summary>Total play time, and the part of it since the last daily reset.</summary>
    public PlaytimeSummary Summarize(DateTime utcNow, TimeSpan resetTimeOfDayUtc)
    {
        var dayStart = DailyReset.Previous(utcNow, resetTimeOfDayUtc);
        var total = TimeSpan.Zero;
        var today = TimeSpan.Zero;
        var count = 0;
        foreach (var (start, length) in Read())
        {
            count++;
            total += length;
            var end = start + length;
            if (end > dayStart)
            {
                today += end - (start > dayStart ? start : dayStart);
            }
        }
        return new PlaytimeSummary(today, total, count);
    }
}
