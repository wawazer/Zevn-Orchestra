using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace ZevnShell;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    internal static AppModel Model { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        // Error tak terduga: catat ke log & tampilkan, jangan tutup app (sesi terminal lain tetap hidup)
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            try { File.AppendAllText(Path.Combine(Config.LocalDir, "error.log"), $"{DateTime.Now:O} {e.Exception}\n\n"); } catch { }
            try { Model?.Flash("Terjadi kesalahan: " + Errors.Text(e.Exception)); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Model = new AppModel();
        Window = new MainWindow();
        Window.Activate();
    }
}
