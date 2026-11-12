// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Nyakke.Core;
using Nyakke.ViewModels;

namespace Nyakke.Views;

/// <summary>Shows findings, or with an empty list a plain question.</summary>
public partial class FindingsWindow : Window
{
    public FindingsWindow()
        : this("", "", [], [], false, false)
    {
    }

    public FindingsWindow(string heading, string intro, IReadOnlyList<Finding> findings,
        IReadOnlyList<LinkItem> links, bool canStart, bool canMarkHandled,
        string startText = "Start anyway", string closeText = "Close")
    {
        InitializeComponent();
        Title = heading;
        Heading.Text = heading;
        Intro.Text = intro;
        List.ItemsSource = findings;
        List.IsVisible = findings.Count > 0;
        CopyButton.IsVisible = findings.Count > 0;
        HandledButton.IsVisible = canMarkHandled;
        StartButton.IsVisible = canStart;
        StartButton.Content = startText;
        CloseButton.Content = closeText;
        foreach (var link in links)
        {
            var button = new Button { Content = link.Name, Classes = { "link" } };
            button.Click += (_, _) => Open(link.Url);
            LinkPanel.Children.Add(button);
        }
        CloseButton.Click += (_, _) => Close(FindingsChoice.Close);
        StartButton.Click += (_, _) => Close(FindingsChoice.StartAnyway);
        HandledButton.Click += (_, _) => Close(FindingsChoice.MarkHandled);
        CopyButton.Click += async (_, _) =>
        {
            var text = new StringBuilder().AppendLine(heading);
            foreach (var finding in findings)
            {
                text.AppendLine().AppendLine(finding.Title).AppendLine(finding.Detail).AppendLine(finding.Path);
            }
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text.ToString());
            }
        };
    }

    private static void Open(string url)
    {
        if (!Settings.IsWebUrl(url))
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
