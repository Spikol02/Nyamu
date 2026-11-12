// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Nyakke.Core;
using Nyakke.ViewModels;

namespace Nyakke.Views;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public Task<FindingsChoice> ShowFindings(string heading, string intro, IReadOnlyList<Finding> findings,
        IReadOnlyList<LinkItem> links, bool canStart, bool canMarkHandled)
    {
        var dialog = new FindingsWindow(heading, intro, findings, links, canStart, canMarkHandled);
        return dialog.ShowDialog<FindingsChoice>(this);
    }

    public async Task<bool> Confirm(string heading, string text, string yes, string no)
    {
        var dialog = new FindingsWindow(heading, text, [], [], canStart: true, canMarkHandled: false,
            startText: yes, closeText: no);
        return await dialog.ShowDialog<FindingsChoice>(this) == FindingsChoice.StartAnyway;
    }

    public Task<Settings?> EditSettings(Settings settings, AppPaths paths)
    {
        return new SettingsWindow(settings, paths).ShowDialog<Settings?>(this);
    }

    public void Minimize() => WindowState = WindowState.Minimized;

    public void Restore()
    {
        WindowState = WindowState.Normal;
        Activate();
    }
}
