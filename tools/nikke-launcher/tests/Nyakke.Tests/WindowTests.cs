// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Nyakke.Core;
using Nyakke.ViewModels;
using Nyakke.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Nyakke.Tests.TestApp))]

namespace Nyakke.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the windows without a screen. With NYAKKE_SHOT_DIR set, the pictures are kept.
/// </summary>
public class WindowTests
{
    private static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var dir = Environment.GetEnvironmentVariable("NYAKKE_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"));
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static (MainWindow Window, MainViewModel Model) Open(TestTree tree, Action<Settings>? change = null)
    {
        var install = tree.MakeInstall();
        var paths = new AppPaths(tree.Dir("data"));
        var settings = new Settings { InstallDir = install.Root };
        change?.Invoke(settings);
        JsonFiles.SaveSettings(paths, settings);
        var window = new MainWindow();
        var model = new MainViewModel(paths, new FakeProcessHost(), window);
        window.DataContext = model;
        window.Show();
        model.Start();
        return (window, model);
    }

    [AvaloniaFact]
    public async Task MainWindowShowsACleanInstall()
    {
        using var tree = new TestTree();
        var (window, model) = Open(tree);
        await Settle();
        Assert.True(model.CanPlay);
        Assert.Equal("PLAY", model.PlayText);
        Assert.Contains("PC build", model.GameRow.Value);
        Assert.StartsWith("No known traces", model.SafetyRow.Value);
        Shot(window, "main");
        model.Shutdown();
        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindowReportsTraces()
    {
        using var tree = new TestTree();
        tree.Text("NIKKE/Launcher/intl_service/debug.log", "\"k2f_chan registered\"\n");
        tree.File("NIKKE/Launcher/log/tinyget/tinyget.x.log", [0x1B, 0x4C, 0x4A, 0x01]);
        var (window, model) = Open(tree);
        await Settle();
        Assert.StartsWith("2 trace(s)", model.SafetyRow.Value);
        Shot(window, "main-traces");
        model.Shutdown();
        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindowWithoutAnInstall()
    {
        using var tree = new TestTree();
        var (window, model) = Open(tree, s => s.InstallDir = Path.Combine(tree.Root, "nowhere"));
        await Settle();
        Assert.False(model.CanPlay);
        Shot(window, "main-no-install");
        model.Shutdown();
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsWindowRendersAndSaves()
    {
        using var tree = new TestTree();
        var install = tree.MakeInstall();
        var window = new SettingsWindow(new Settings { InstallDir = install.Root }, new AppPaths(tree.Dir("data")));
        window.Show();
        Shot(window, "settings");
        window.Close();
    }

    [AvaloniaFact]
    public void FindingsWindowRenders()
    {
        Finding[] findings =
        [
            new(FindingKind.AgentLog, "The launcher log shows the remote-control agent started",
                "3 line(s) with \"k2f_chan\", the first at byte 88123.", @"C:\NIKKE\Launcher\intl_service\debug.log"),
            new(FindingKind.PersistenceRegistry, "A registry entry that loads the attacker's DLL is present",
                "Remove it by following the official manual check, then restart Windows.",
                @"HKCU\Software\Classes\CLSID\{E77CC89B-7401-4C04-8CED-149DB35ADD04}"),
        ];
        var window = new FindingsWindow("Known traces were found", "These are traces the November 2026 attack " +
            "on the official launcher is known to leave.", findings, Settings.DefaultLinks(), true, true);
        window.Show();
        Shot(window, "findings");
        window.Close();
    }
}
