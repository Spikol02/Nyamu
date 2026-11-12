// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace Nyakke.Core;

public sealed class TripwireAlert
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime SeenUtc { get; set; }
    public string HeaderHex { get; set; } = "";
    public string? EvidenceCopy { get; set; }
    public List<string> ClosedPrograms { get; set; } = [];
}

/// <summary>What the session agent tells the window. Written to a file the window re-reads.</summary>
public sealed class SessionStatus
{
    public int AgentPid { get; set; }
    public SessionPhase Phase { get; set; }
    public SessionOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
    public int? GamePid { get; set; }
    public int? CloseCountdown { get; set; }
    public bool Finished { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<string> Notes { get; set; } = [];
    public TripwireAlert? Alert { get; set; }

    public static void Write(string path, SessionStatus status)
    {
        status.UpdatedUtc = DateTime.UtcNow;
        FileUtil.WriteAllTextAtomic(path, JsonSerializer.Serialize(status, NyakkeJson.Default.SessionStatus));
    }

    public static SessionStatus? TryRead(string path)
    {
        var text = FileUtil.TryReadAllTextShared(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(text, NyakkeJson.Default.SessionStatus);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
