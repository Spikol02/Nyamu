// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nyakke.Core;

/// <summary>The few things Nyakke does to other programs. Tests swap in a fake.</summary>
public interface IProcessHost
{
    IReadOnlyList<ProcessInfo> Snapshot();

    /// <summary>Starts a program the way a double-click does.</summary>
    bool Start(string exe, string workingDir, out string? error);

    /// <summary>Asks a program to close its window.</summary>
    bool CloseMainWindow(int pid);

    bool Kill(int pid, out string? error);
}

public sealed partial class SystemProcessHost : IProcessHost
{
    private readonly Dictionary<int, (string Name, string? Path)> pathCache = [];

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        var result = new List<ProcessInfo>();
        var alive = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = process.Id;
                var name = process.ProcessName;
                if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".exe";
                }
                alive.Add(pid);
                if (!pathCache.TryGetValue(pid, out var cached) || cached.Name != name)
                {
                    cached = (name, QueryPath(process));
                    pathCache[pid] = cached;
                }
                result.Add(new ProcessInfo(pid, name, cached.Path));
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // The process ended while it was being read.
            }
            finally
            {
                process.Dispose();
            }
        }
        foreach (var gone in pathCache.Keys.Where(pid => !alive.Contains(pid)).ToList())
        {
            pathCache.Remove(gone);
        }
        return result;
    }

    public bool Start(string exe, string workingDir, out string? error)
    {
        error = null;
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = true,
            };
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            error = e.Message;
            return false;
        }
    }

    public bool CloseMainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.CloseMainWindow();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception
                                      or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public bool Kill(int pid, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            return true;
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            error = e.Message;
            return false;
        }
    }

    private static string? QueryPath(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return QueryPathWindows(process.Id);
        }
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks with the "limited information" right, which also works for a launcher that runs as
    /// administrator while Nyakke does not.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe string? QueryPathWindows(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }
        try
        {
            const int Capacity = 2048;
            var buffer = stackalloc char[Capacity];
            var size = (uint)Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
