// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Nyakke.Core;
using Nyakke.ViewModels;
using Nyakke.Views;

namespace Nyakke;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var model = new MainViewModel(AppPaths.Default(), new SystemProcessHost(), window);
            window.DataContext = model;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => model.Shutdown();
            model.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
