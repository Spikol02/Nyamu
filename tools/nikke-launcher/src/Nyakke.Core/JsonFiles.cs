// SPDX-FileCopyrightText: Copyright 2026 Eden Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nyakke.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(SessionStatus))]
public sealed partial class NyakkeJson : JsonSerializerContext
{
}

public static class JsonFiles
{
    /// <summary>
    /// Loads a JSON file. A missing file gives <paramref name="fallback"/>; a file that cannot be
    /// read is kept as *.bad and reported in <paramref name="warning"/>.
    /// </summary>
    public static T Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> fallback, out string? warning)
        where T : class
    {
        warning = null;
        if (!File.Exists(path))
        {
            return fallback();
        }
        try
        {
            var text = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize(text, typeInfo);
            if (value is not null)
            {
                return value;
            }
            warning = Path.GetFileName(path) + " is empty.";
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            warning = Path.GetFileName(path) + " could not be read: " + e.Message;
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
            }
        }
        return fallback();
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        FileUtil.WriteAllTextAtomic(path, JsonSerializer.Serialize(value, typeInfo) + Environment.NewLine);
    }

    public static Settings LoadSettings(AppPaths paths, out string? warning)
    {
        var settings = Load(paths.SettingsFile, NyakkeJson.Default.Settings, () => new Settings(), out warning);
        settings.Normalize();
        return settings;
    }

    public static void SaveSettings(AppPaths paths, Settings settings)
    {
        settings.Normalize();
        Save(paths.SettingsFile, settings, NyakkeJson.Default.Settings);
    }

    public static AppState LoadState(AppPaths paths)
    {
        var state = Load(paths.StateFile, NyakkeJson.Default.AppState, () => new AppState(), out _);
        state.DebugLogBaselines ??= [];
        state.AcknowledgedFindings ??= [];
        state.CacheResetDone ??= [];
        return state;
    }

    public static void SaveState(AppPaths paths, AppState state)
    {
        Save(paths.StateFile, state, NyakkeJson.Default.AppState);
    }
}
