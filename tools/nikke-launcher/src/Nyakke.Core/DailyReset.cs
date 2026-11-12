// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace Nyakke.Core;

/// <summary>The game's daily reset, kept as a time of day in UTC.</summary>
public static class DailyReset
{
    /// <summary>20:00 UTC (05:00 UTC+9).</summary>
    public const string DefaultText = "20:00";

    public static bool TryParse(string? text, out TimeSpan timeOfDayUtc)
    {
        timeOfDayUtc = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            hours > 23 || minutes > 59)
        {
            return false;
        }
        timeOfDayUtc = new TimeSpan(hours, minutes, 0);
        return true;
    }

    public static TimeSpan ParseOrDefault(string? text)
    {
        return TryParse(text, out var value) ? value : new TimeSpan(20, 0, 0);
    }

    /// <summary>The first reset after <paramref name="utcNow"/>.</summary>
    public static DateTime Next(DateTime utcNow, TimeSpan timeOfDayUtc)
    {
        var today = utcNow.Date + timeOfDayUtc;
        return today > utcNow ? today : today.AddDays(1);
    }

    /// <summary>The latest reset at or before <paramref name="utcNow"/>.</summary>
    public static DateTime Previous(DateTime utcNow, TimeSpan timeOfDayUtc)
    {
        return Next(utcNow, timeOfDayUtc).AddDays(-1);
    }
}

public static class DurationText
{
    /// <summary>"5h 12m", "12m 03s" or "45s".</summary>
    public static string Short(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        var hours = (long)span.TotalHours;
        if (hours > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hours}h {span.Minutes:00}m");
        }
        if (span.Minutes > 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m {span.Seconds:00}s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{span.Seconds}s");
    }

    /// <summary>"1:02:03" for a running clock.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{(long)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
