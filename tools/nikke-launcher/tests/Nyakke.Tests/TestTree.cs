// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Nyakke.Core;

namespace Nyakke.Tests;

/// <summary>A throwaway folder that can hold a stand-in NIKKE install.</summary>
public sealed class TestTree : IDisposable
{
    public TestTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "nyakke-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relative, byte[]? content = null)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content ?? []);
        return path;
    }

    public string Text(string relative, string content)
    {
        return File(relative, System.Text.Encoding.UTF8.GetBytes(content));
    }

    /// <summary>NIKKE/Launcher/nikke_launcher.exe and NIKKE/NIKKE/game/nikke.exe.</summary>
    public NikkeInstall MakeInstall(string name = "NIKKE", bool withGame = true, string launcherExe = "nikke_launcher.exe")
    {
        File($"{name}/Launcher/{launcherExe}", "MZ"u8.ToArray());
        if (withGame)
        {
            File($"{name}/NIKKE/game/nikke.exe", "MZ"u8.ToArray());
        }
        return NikkeInstall.Probe(Path.Combine(Root, name)) ?? throw new InvalidOperationException("no install");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A process list that tests change by hand.</summary>
public sealed class FakeProcessHost : IProcessHost
{
    private readonly Lock gate = new();
    private readonly List<ProcessInfo> processes = [];
    private int nextPid = 1000;

    public List<string> Started { get; } = [];
    public List<int> AskedToClose { get; } = [];
    public List<int> Killed { get; } = [];

    /// <summary>Called when a program is started; return false to make the start fail.</summary>
    public Func<string, bool>? OnStart { get; set; }

    /// <summary>Return true when the process closes itself on being asked.</summary>
    public Func<ProcessInfo, bool>? ClosesOnRequest { get; set; }

    /// <summary>Return false when ending the process is refused.</summary>
    public Func<ProcessInfo, bool>? AllowsKill { get; set; }

    public ProcessInfo Add(string path)
    {
        lock (gate)
        {
            var info = new ProcessInfo(nextPid++, Path.GetFileName(path.Replace('\\', '/')), path);
            processes.Add(info);
            return info;
        }
    }

    public ProcessInfo AddNamed(string name)
    {
        lock (gate)
        {
            var info = new ProcessInfo(nextPid++, name, null);
            processes.Add(info);
            return info;
        }
    }

    public void Remove(int pid)
    {
        lock (gate)
        {
            processes.RemoveAll(p => p.Pid == pid);
        }
    }

    public bool IsRunning(int pid)
    {
        lock (gate)
        {
            return processes.Any(p => p.Pid == pid);
        }
    }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (gate)
        {
            return processes.ToList();
        }
    }

    public bool Start(string exe, string workingDir, out string? error)
    {
        error = null;
        Started.Add(exe);
        if (OnStart is not null && !OnStart(exe))
        {
            error = "start refused";
            return false;
        }
        return true;
    }

    public bool CloseMainWindow(int pid)
    {
        ProcessInfo? target;
        lock (gate)
        {
            target = processes.FirstOrDefault(p => p.Pid == pid);
            AskedToClose.Add(pid);
        }
        if (target is not null && ClosesOnRequest?.Invoke(target) == true)
        {
            Remove(pid);
        }
        return target is not null;
    }

    public bool Kill(int pid, out string? error)
    {
        error = null;
        ProcessInfo? target;
        lock (gate)
        {
            target = processes.FirstOrDefault(p => p.Pid == pid);
        }
        if (target is not null && AllowsKill?.Invoke(target) == false)
        {
            error = "Access is denied.";
            return false;
        }
        lock (gate)
        {
            Killed.Add(pid);
        }
        Remove(pid);
        return true;
    }
}
