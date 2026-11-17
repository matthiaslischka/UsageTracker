using UsageTracker.Infrastructure;
using UsageTracker.UI;

namespace UsageTracker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\UsageTracker-7b1f0c2e", out var isFirst);
        if (!isFirst) return; // already running

        Directory.CreateDirectory(AppPaths.Directory);
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        using var app = new TrayApp();
        Application.Run(app);
    }
}
