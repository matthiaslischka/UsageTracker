using Microsoft.Win32;
using UsageTracker.Core;

namespace UsageTracker.Infrastructure;

internal static class AppPaths
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageTracker");

    public static string Settings => Path.Combine(Directory, "settings.json");
    public static string State => Path.Combine(Directory, "state.json");
    public static string Log => Path.Combine(Directory, "log.txt");

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Minimal error log. Never pass tokens or response bodies here.</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AppPaths.Directory);
                var info = new FileInfo(AppPaths.Log);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(AppPaths.Log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class SettingsStore
{
    /// <summary>Loads settings, creating the file with defaults when missing. Returns null if the file is invalid.</summary>
    public static AppSettings? Load(out bool createdNow)
    {
        createdNow = false;
        if (!File.Exists(AppPaths.Settings))
        {
            var defaults = new AppSettings();
            JsonFiles.WriteAtomic(AppPaths.Settings, JsonFiles.Serialize(defaults));
            createdNow = true;
            return defaults;
        }

        try
        {
            var settings = JsonFiles.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings));
            if (settings is null) return null;
            settings.Providers ??= new();
            settings.Copilot ??= new();
            settings.Copilot.Accounts ??= [];
            settings.Toasts ??= new();
            settings.Toasts.RemainingThresholds ??= [];
            settings.Toasts.CopilotDailyUsedThresholds ??= [];
            return settings;
        }
        catch (IOException ex)
        {
            Log.Write($"settings read failed: {ex.Message}");
            return null;
        }
    }
}

internal static class StateStore
{
    public static AppState Load()
    {
        try
        {
            if (File.Exists(AppPaths.State) &&
                JsonFiles.Deserialize<AppState>(File.ReadAllText(AppPaths.State)) is { } state)
            {
                state.CopilotSnapshots ??= [];
                state.Alerts ??= [];
                return state;
            }
        }
        catch (IOException ex)
        {
            Log.Write($"state read failed: {ex.Message}");
        }
        return new AppState();
    }

    public static void Save(AppState state)
    {
        try
        {
            JsonFiles.WriteAtomic(AppPaths.State, JsonFiles.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"state write failed: {ex.Message}");
        }
    }
}

/// <summary>"Start with Windows" via the HKCU Run key.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageTracker";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the registered path pointing at this exe if the user moved it.</summary>
    public static void RefreshPathIfEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(ValueName, Command);
    }
}
