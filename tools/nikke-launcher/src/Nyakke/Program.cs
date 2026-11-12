// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using Avalonia;
using Nyakke.Core;

namespace Nyakke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The session agent is this same program without a window. It must not touch the UI
        // libraries, so everything Avalonia stays behind RunWindow.
        if (args.Length > 0 && args[0] == AgentOptions.Switch)
        {
            return RunAgent(args);
        }
        return RunWindow(args);
    }

    private static int RunAgent(string[] args)
    {
        Log log = Log.None;
        try
        {
            var options = AgentOptions.Parse(args);
            log = new Log(options.LogFile);
            return new AgentRunner(options, new SystemProcessHost(), log).Run();
        }
        catch (Exception e)
        {
            log.Write("agent: crashed: " + e);
            return AgentRunner.ExitFailed;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWindow(string[] args)
    {
        using var single = new Mutex(true, "Local\\Nyakke.Window", out var first);
        if (!first)
        {
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
