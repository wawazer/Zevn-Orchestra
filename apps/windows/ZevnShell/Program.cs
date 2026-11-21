using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using uniffi.zevn_core;

namespace ZevnShell;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 1. Mode ProxyCommand: ssh.exe -o ProxyCommand="ZevnShell.exe --zevn-proxy <socks5|http> <host> <port> %h %p".
        //    Salurkan stdin/stdout ke server lewat proxy, tanpa UI. Dicek sebelum askpass karena
        //    environment askpass (ZEVN_ASKPASS) dari ssh.exe ikut terwarisi ke proses ini.
        if (args.Length >= 6 && args[0] == "--zevn-proxy")
        {
            var proxy = new HostProxy(args[1] == "http" ? ProxyKind.Http : ProxyKind.Socks5, args[2],
                ushort.TryParse(args[3], out var pp) ? pp : (ushort)1080,
                Environment.GetEnvironmentVariable("ZEVN_PROXY_USER"), Environment.GetEnvironmentVariable("ZEVN_PROXY_PASS"));
            try
            {
                ZevnCoreMethods.ProxyStdio(proxy, args[4], ushort.TryParse(args[5], out var tp) ? tp : (ushort)22);
                return 0;
            }
            catch (Exception e)
            {
                using var err = Console.OpenStandardError();
                var b = Encoding.UTF8.GetBytes("Zevn Shell: " + Errors.Text(e) + "\n");
                err.Write(b, 0, b.Length);
                return 1;
            }
        }

        // 2. Mode askpass: ssh.exe memanggil ZevnShell.exe untuk meminta password/passphrase host.
        if (Environment.GetEnvironmentVariable("ZEVN_ASKPASS") == "1")
        {
            var prompt = string.Join(" ", args);
            if (!Regex.IsMatch(prompt, "assword|assphrase|PIN", RegexOptions.IgnoreCase)) return 1;
            using var o = Console.OpenStandardOutput();
            var b = Encoding.UTF8.GetBytes((Environment.GetEnvironmentVariable("ZEVN_SECRET") ?? "") + "\n");
            o.Write(b, 0, b.Length);
            return 0;
        }

        // 3. Redirect login Google dari browser: teruskan ke instance yang sedang jalan.
        if (args.FirstOrDefault(a => a.StartsWith(GoogleAuth.Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } url)
        {
            GoogleAuth.ForwardToRunningInstance(url);
            return 0;
        }

        // Data WebView2 di %LOCALAPPDATA% (folder app bisa saja read-only)
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(Config.LocalDir, "WebView2"));
        SshCommand.CleanupStaleKeys();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
