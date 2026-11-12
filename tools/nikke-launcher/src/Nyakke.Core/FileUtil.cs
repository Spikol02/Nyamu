// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace Nyakke.Core;

public static class FileUtil
{
    /// <summary>
    /// Child of <paramref name="dir"/> named <paramref name="name"/>, ignoring case. Windows does
    /// this by itself; the lookup keeps installs readable from other systems (tests, Proton).
    /// </summary>
    public static string? FindChild(string dir, string name)
    {
        try
        {
            var direct = Path.Combine(dir, name);
            if (File.Exists(direct) || Directory.Exists(direct))
            {
                return direct;
            }
            if (!Directory.Exists(dir))
            {
                return null;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Follows <paramref name="parts"/> from <paramref name="dir"/>, ignoring case.</summary>
    public static string? FindPath(string dir, params string[] parts)
    {
        string? current = dir;
        foreach (var part in parts)
        {
            current = FindChild(current, part);
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>Compares paths the way Windows does: case and slash direction do not matter.</summary>
    public static string NormalizeForCompare(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        return normalized.ToUpperInvariant();
    }

    public static bool SamePath(string a, string b) => NormalizeForCompare(a) == NormalizeForCompare(b);

    /// <summary>True when <paramref name="path"/> is inside <paramref name="dir"/> (not the folder itself).</summary>
    public static bool IsUnder(string path, string dir)
    {
        var p = NormalizeForCompare(path);
        var d = NormalizeForCompare(dir);
        return p.Length > d.Length + 1 && p.StartsWith(d, StringComparison.Ordinal) && p[d.Length] == '\\';
    }

    /// <summary>Case-insensitive match with * and ? wildcards.</summary>
    public static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' ||
                char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    /// <summary>Writes through a temporary file so a reader never sees half a file.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A reader holds the target open: fall back to writing in place.
            File.WriteAllText(path, text, new UTF8Encoding(false));
            TryDelete(temp);
        }
    }

    /// <summary>Reads a file another process may be writing.</summary>
    public static string? TryReadAllTextShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>First bytes of a file, even while its writer still has it open.</summary>
    public static int ReadHeader(string path, Span<byte> buffer)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    public static long DirectorySize(string dir, int maxFiles = 20000)
    {
        long total = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            var count = 0;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                total += file.Length;
                if (++count >= maxFiles)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }
}
