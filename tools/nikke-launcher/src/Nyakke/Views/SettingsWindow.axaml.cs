// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nyakke.Core;

namespace Nyakke.Views;

public partial class SettingsWindow : Window
{
    private readonly Settings settings;

    public SettingsWindow()
        : this(new Settings(), AppPaths.Default())
    {
    }

    public SettingsWindow(Settings settings, AppPaths paths)
    {
        this.settings = settings;
        InitializeComponent();
        InstallBox.Text = settings.InstallDir;
        CloseBox.IsChecked = settings.CloseLauncherAfterGameStart;
        DelayBox.Value = settings.CloseDelaySeconds;
        CheckBox.IsChecked = settings.CheckBeforeStart;
        WatchBox.IsChecked = settings.WatchDownloads;
        MinimizeBox.IsChecked = settings.MinimizeWhileGameRuns;
        CacheBox.SelectedIndex = settings.ResetWebCache switch
        {
            CacheResetMode.Once => 0,
            CacheResetMode.Always => 1,
            _ => 2,
        };
        DirectBox.IsChecked = settings.Mode == LaunchMode.Direct;
        BackgroundBox.Text = settings.BackgroundImage;
        TitleBox.Text = settings.Title;
        ResetBox.Text = settings.DailyResetUtc;
        ShowInstall();

        InstallBox.TextChanged += (_, _) => ShowInstall();
        BrowseInstall.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the NIKKE folder (the one that holds \"Launcher\")",
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                InstallBox.Text = NikkeInstall.Probe(path)?.Root ?? path;
            }
        };
        DetectInstall.Click += (_, _) =>
        {
            var found = InstallDiscovery.FindAll();
            InstallBox.Text = found.Count > 0 ? found[0].Root : "";
            if (found.Count == 0)
            {
                InstallNote.Text = "No NIKKE install was found in the usual places. Use Browse.";
            }
        };
        BrowseBackground.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a background picture",
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                BackgroundBox.Text = path;
            }
        };
        ClearBackground.Click += (_, _) => BackgroundBox.Text = "";
        DataFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(paths.DataDir);
                using var _ = Process.Start(new ProcessStartInfo(paths.DataDir) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        };
        CancelButton.Click += (_, _) => Close(null);
        SaveButton.Click += (_, _) => Save();
    }

    private void ShowInstall()
    {
        var text = InstallBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            InstallNote.Text = "Empty: Nyakke looks in the places the official launcher records and in the usual folders.";
            return;
        }
        var install = NikkeInstall.Probe(text);
        InstallNote.Text = install is null
            ? "This folder holds no Launcher\\nikke_launcher.exe."
            : install.Flavor + " in " + install.Root +
              (install.GameExe is null ? " (the game itself is not downloaded yet)" : "");
    }

    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(InstallBox.Text) && NikkeInstall.Probe(InstallBox.Text) is null)
        {
            ErrorText.Text = "The NIKKE folder is not an install.";
            return;
        }
        if (!DailyReset.TryParse(ResetBox.Text, out _))
        {
            ErrorText.Text = "Write the daily reset as HH:mm, for example 20:00.";
            return;
        }
        settings.InstallDir = NikkeInstall.Probe(InstallBox.Text)?.Root;
        settings.CloseLauncherAfterGameStart = CloseBox.IsChecked == true;
        settings.CloseDelaySeconds = (int)(DelayBox.Value ?? 20);
        settings.CheckBeforeStart = CheckBox.IsChecked == true;
        settings.WatchDownloads = WatchBox.IsChecked == true;
        settings.MinimizeWhileGameRuns = MinimizeBox.IsChecked == true;
        settings.ResetWebCache = CacheBox.SelectedIndex switch
        {
            0 => CacheResetMode.Once,
            1 => CacheResetMode.Always,
            _ => CacheResetMode.Never,
        };
        settings.Mode = DirectBox.IsChecked == true ? LaunchMode.Direct : LaunchMode.OfficialLauncher;
        settings.BackgroundImage = BackgroundBox.Text;
        settings.Title = TitleBox.Text ?? "";
        settings.DailyResetUtc = ResetBox.Text!.Trim();
        settings.Normalize();
        Close(settings);
    }
}
