using System.Text.Json;
using System.Text.Json.Serialization;

namespace UsageTracker.Core;

public sealed class AppSettings
{
    public double PollIntervalMinutes { get; set; } = 5;

    public ProviderToggles Providers { get; set; } = new();

    public CopilotSettings Copilot { get; set; } = new();

    public ToastSettings Toasts { get; set; } = new();

    [JsonIgnore]
    public TimeSpan PollInterval => TimeSpan.FromMinutes(Math.Clamp(PollIntervalMinutes, 1, 24 * 60));
}

public sealed class ProviderToggles
{
    public bool Claude { get; set; } = true;
    public bool Codex { get; set; } = true;
    public bool Copilot { get; set; } = true;
}

public sealed class CopilotSettings
{
    public List<CopilotAccount> Accounts { get; set; } = [new CopilotAccount { Host = "github.com", Login = "" }];
    public bool ExcludeWeekends { get; set; } = true;
}

public sealed class CopilotAccount
{
    public string Host { get; set; } = "";
    public string Login { get; set; } = "";
}

public sealed class ToastSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Notify when a window's remaining % drops below each of these.</summary>
    public List<double> RemainingThresholds { get; set; } = [20, 5];

    /// <summary>Notify when this % of Copilot's daily budget has been used.</summary>
    public List<double> CopilotDailyUsedThresholds { get; set; } = [80, 100];

    /// <summary>Notify when a window resets.</summary>
    public bool ResetToast { get; set; }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Writes via a temp file so readers never see a half-written file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
