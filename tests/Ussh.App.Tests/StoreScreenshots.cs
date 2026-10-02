using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ussh.App.ViewModels;
using Ussh.Core.Models;
using Ussh.Core.Ssh;
using Ussh.Core.Tests.Integration;

namespace Ussh.App.Tests;

/// <summary>
/// Microsoft Store screenshots: the real app, rendered at 1920×1080 with staged demo content
/// (fictional servers, a demo user, canned terminal output and a demo folder; nothing from this
/// machine). A one-off: runs only when ZSSH_STORE_SCREENSHOTS names an output folder, e.g.
///   ZSSH_STORE_SCREENSHOTS=packaging/store/screenshots dotnet test tests/Ussh.App.Tests --filter StoreScreenshots
/// </summary>
public sealed partial class UiTests
{
    private const int StoreWidth = 1920;
    private const int StoreHeight = 1080;

    [AvaloniaFact]
    public async Task StoreScreenshots()
    {
        var output = Environment.GetEnvironmentVariable("ZSSH_STORE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
            return; // only on request
        Directory.CreateDirectory(output);

        var web1 = SshTestServer.TryStart(sftp: true, port: 2201);
        var web2 = SshTestServer.TryStart(port: 2202);
        var bastion = SshTestServer.TryStart(port: 2200);
        var db = SshTestServer.TryStart(port: 2203);
        Assert.True(web1 != null && web2 != null && bastion != null && db != null, "python3 with paramiko is needed for screenshots");
        _dispose.AddRange(new IDisposable[] { web1!, web2!, bastion!, db! });
        var scripts = WriteDemoScripts();
        var demoHome = CreateDemoFolders(web1!.SftpRoot!);

        var (window, vm, sessions) = Create();
        window.Width = StoreWidth;
        window.Height = StoreHeight;
        await CreateVault(vm);

        // ---- Servers: a realistic, fictional estate ----
        var web01 = DemoServer(vm, "web-01", "Production", web1.Port, theme: null);
        var web02 = DemoServer(vm, "web-02", "Production", web2.Port, theme: null);
        var bastionProfile = DemoServer(vm, "bastion", "Production", bastion.Port, theme: null);
        var db01 = DemoServer(vm, "db-01", "Production", db.Port, theme: "Tomorrow Night");
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single(s => s.Name == "db-01");
        vm.Servers.EditServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedJumpHost = editor.JumpHostOptions.Single(o => o.Id == bastionProfile.Id);
        editor.AddTunnelCommand.Execute(null);
        (editor.Tunnels[0].BindPort, editor.Tunnels[0].DestinationPort) = (15432, 5432);
        editor.Notes = "Primary PostgreSQL. Reach it via the bastion; the tunnel exposes it on localhost:15432.";
        vm.Servers.SaveServerCommand.Execute(null);
        var amber = DemoServer(vm, "build-01", "CI runners", web1.Port, "Amber (P3 phosphor)");
        var green = DemoServer(vm, "build-02", "CI runners", web2.Port, "Green (P1 phosphor)");
        var solarized = DemoServer(vm, "build-03", "CI runners", web1.Port, "Solarized Dark");
        var dracula = DemoServer(vm, "build-04", "CI runners", web2.Port, "Dracula");
        DemoServer(vm, "media-drop", "Partners", web1.Port, theme: null, kind: ServerKind.SftpOnly);
        var s3 = S3TestServer.TryStart();
        ServerProfile? bucket = null;
        if (s3 != null)
        {
            _dispose.Add(s3);
            foreach (var name in new[] { "company-backups", "website-assets", "app-logs" })
                s3.CreateBucket(name);
            bucket = s3.CreateBucket("release-artifacts");
            vm.Servers.AddServerCommand.Execute(null);
            var s3Editor = vm.Servers.Editor!;
            s3Editor.SelectedKind = s3Editor.KindOptions.Single(k => k.Kind == ServerKind.S3);
            (s3Editor.Name, s3Editor.Group, s3Editor.S3Region, s3Editor.S3AccessKeyId, s3Editor.S3SecretAccessKey, s3Editor.S3ServiceUrl) =
                ("AWS (all buckets)", "Cloud", "eu-west-2", "AKIAEXAMPLE", "secret", s3.Url);
            vm.Servers.SaveServerCommand.Execute(null);
        }

        // ---- 1. A terminal session ----
        vm.Connect(web01);
        var session = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => session.FocusedPane.Session.State == SessionState.Connected, "web-01");
        await Pump(300);
        await RunDemo(session.FocusedPane, "web-01", scripts["web"]);
        await StoreShot(window, output, "01-terminal");

        // ---- 2. Two load-balanced nodes side by side, typing into both ----
        vm.ConnectTogether(new[] { web01, web02 });
        var pair = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => pair.Panes.All(p => p.Session.State == SessionState.Connected), "pair");
        await Pump(300);
        await SetPrompt(pair.Panes[0], "web-01");
        await SetPrompt(pair.Panes[1], "web-02");
        pair.IsBroadcasting = true;
        pair.FocusedPane.SendInput($"clear; bash {scripts["node"]}\r");
        await WaitUntil(() => pair.Panes.All(p => Screen(p).Contains("deploy complete")), "broadcast output");
        await Pump(400);
        await StoreShot(window, output, "02-split-broadcast");
        pair.IsBroadcasting = false;

        // ---- 3. Per-server themes: four panes, four looks ----
        vm.ConnectTogether(new[] { amber, green, solarized, dracula });
        var themes = (TerminalTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => themes.Panes.All(p => p.Session.State == SessionState.Connected), "theme grid");
        await Pump(300);
        foreach (var (pane, name) in themes.Panes.Zip(new[] { "build-01", "build-02", "build-03", "build-04" }))
            await SetPrompt(pane, name);
        themes.IsBroadcasting = true;
        themes.FocusedPane.SendInput($"clear; bash {scripts["build"]}\r");
        await WaitUntil(() => themes.Panes.All(p => Screen(p).Contains("Build succeeded")), "build output");
        themes.IsBroadcasting = false;
        await Pump(400);
        await StoreShot(window, output, "03-themes");

        // ---- 4. Server management (read-only view, jump host, tunnel) ----
        vm.SelectedTab = vm.Servers;
        vm.Servers.SelectedServer = vm.Servers.FilteredServers.Single(s => s.Name == "db-01");
        await Pump(300);
        await StoreShot(window, output, "04-servers");

        // ---- 5. Dual-pane SFTP browser with finished transfers ----
        vm.OpenFiles(web01);
        var files = (FilesTabViewModel)vm.SelectedTab!;
        await WaitUntil(() => files.Remote.CurrentPath.Length > 0 && files.Local.CurrentPath.Length > 0, "file panes");
        await files.Local.NavigateAsync(Path.Combine(demoHome, "website"));
        await files.Remote.NavigateAsync("/var/www/example.com");
        files.Local.Selected = files.Local.Rows.Where(r => r.Name is "assets" or "index.html" or "about.html").ToList();
        await files.UploadCommand.ExecuteAsync(null);
        await WaitUntil(() => files.Queue.Items.Count > 0 && files.Queue.ActiveCount == 0, "uploads");
        await WaitUntil(() => files.Remote.Rows.Any(r => r.Name == "about.html"), "remote refresh");
        await Pump(600);
        files.Remote.Selected = files.Remote.Rows.Where(r => r.Name == "assets").ToList();
        await Pump(200);
        await StoreShot(window, output, "05-files-sftp");

        // ---- 6. S3: all buckets, then inside one ----
        if (bucket != null)
        {
            var aws = vm.Data!.Servers.Single(s => s.Kind == ServerKind.S3);
            vm.OpenFiles(aws);
            var s3Tab = (FilesTabViewModel)vm.SelectedTab!;
            await WaitUntil(() => s3Tab.Remote.Rows.Count >= 4, "bucket list");
            await s3Tab.Local.NavigateAsync(Path.Combine(demoHome, "releases"));
            await s3Tab.Remote.NavigateAsync("/release-artifacts");
            s3Tab.Local.Selected = s3Tab.Local.Rows.ToList();
            await s3Tab.UploadCommand.ExecuteAsync(null);
            await WaitUntil(() => s3Tab.Queue.Items.Count > 0 && s3Tab.Queue.ActiveCount == 0, "s3 uploads");
            await WaitUntil(() => s3Tab.Remote.Rows.Count >= 2, "bucket refresh");
            await Pump(600);
            await StoreShot(window, output, "06-files-s3");
        }

        // ---- 7. Locked: sessions keep running behind the admin password ----
        vm.SelectedTab = session;
        vm.Lock();
        await Pump(300);
        await StoreShot(window, output, "07-locked");

        foreach (var tab in vm.Tabs.OfType<TerminalTabViewModel>())
            foreach (var pane in tab.Panes)
                pane.Session.Disconnect();
        await sessions.DisposeAsync();
    }

    private static ServerProfile DemoServer(MainWindowViewModel vm, string name, string group, int port, string? theme,
        ServerKind kind = ServerKind.Ssh)
    {
        vm.Servers.AddServerCommand.Execute(null);
        var editor = vm.Servers.Editor!;
        editor.SelectedKind = editor.KindOptions.Single(k => k.Kind == kind);
        (editor.Name, editor.Group, editor.Host, editor.Port, editor.Username, editor.Password) =
            (name, group, "localhost", port, "deploy", SshTestServer.Password);
        if (theme != null)
            editor.SelectedTheme = editor.ThemeOptions.Single(o => o.Name == theme);
        vm.Servers.SaveServerCommand.Execute(null);
        Assert.Null(editor.ValidationError);
        return vm.Data!.Servers.Single(s => s.Id == editor.Id);
    }

    private static async Task SetPrompt(TerminalPaneViewModel pane, string host)
    {
        pane.Session.Send($"export DEMO_HOST={host} PS1='\\[\\e[1;32m\\]deploy@{host}\\[\\e[0m\\]:\\[\\e[1;34m\\]~\\[\\e[0m\\]$ '; clear\r");
        await Pump(300);
    }

    private static async Task RunDemo(TerminalPaneViewModel pane, string host, string script)
    {
        await SetPrompt(pane, host);
        pane.Session.Send($"clear; bash {script}\r");
        await WaitUntil(() => Screen(pane).Contains("200 OK"), "demo output");
        await Pump(400);
    }

    private static string Screen(TerminalPaneViewModel pane)
    {
        lock (pane.Session.Emulator.SyncRoot)
        {
            var buffer = pane.Session.Emulator.Terminal.Buffer;
            return string.Join("\n", Enumerable.Range(0, buffer.Lines.Length)
                .Select(i => buffer.TranslateBufferLineToString(i, true, 0, -1).ToString()));
        }
    }

    private static async Task StoreShot(Window window, string folder, string name)
    {
        await Pump(200);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame()!;
        Assert.True(frame.PixelSize.Width >= 1366 && frame.PixelSize.Height >= 768, $"{name}: {frame.PixelSize}");
        var path = Path.Combine(folder, name + ".png");
        frame.Save(path);
        Assert.True(new FileInfo(path).Length < 50L * 1024 * 1024);
    }

    /// <summary>Canned terminal sessions: only printf, so nothing real from this machine is shown.</summary>
    private Dictionary<string, string> WriteDemoScripts()
    {
        var dir = Path.Combine(_dir, "demo-scripts");
        Directory.CreateDirectory(dir);
        const string prelude = """
            p() { printf '\e[1;32mdeploy@%s\e[0m:\e[1;34m%s\e[0m$ %s\n' "$1" "$2" "$3"; }
            g=$'\e[32m'; y=$'\e[33m'; b=$'\e[34m'; c=$'\e[36m'; m=$'\e[35m'; r=$'\e[31m'; B=$'\e[1m'; d=$'\e[2m'; z=$'\e[0m'
            """;
        var scripts = new Dictionary<string, string>
        {
            ["web"] = prelude + """

                p web-01 '~' 'uptime'
                echo " 14:32:07 up 87 days,  3:12,  2 users,  load average: 0.42, 0.38, 0.35"
                p web-01 '~' 'systemctl status nginx --no-pager'
                echo "${g}●${z} nginx.service - A high performance web server and a reverse proxy server"
                echo "     Loaded: loaded (/lib/systemd/system/nginx.service; ${g}enabled${z}; preset: ${g}enabled${z})"
                echo "     Active: ${g}${B}active (running)${z} since Mon 2026-07-07 11:20:41 UTC; 2 months 27 days ago"
                echo "       Docs: man:nginx(8)"
                echo "   Main PID: 1123 (nginx)"
                echo "      Tasks: 5 (limit: 9387)"
                echo "     Memory: 18.4M (peak: 22.1M)"
                echo "        CPU: 4h 12min 31.207s"
                echo "     CGroup: /system.slice/nginx.service"
                echo "             ├─1123 \"nginx: master process /usr/sbin/nginx -g daemon on; master_process on;\""
                echo "             └─1124 \"nginx: worker process\""
                p web-01 '~' 'ls -l /var/www/example.com'
                echo "total 32"
                echo "drwxr-xr-x 4 www-data www-data 4096 Oct  1 09:14 ${b}${B}assets${z}"
                echo "-rw-r--r-- 1 www-data www-data 6214 Oct  1 09:14 about.html"
                echo "-rw-r--r-- 1 www-data www-data 9832 Oct  1 09:14 index.html"
                echo "-rwxr-xr-x 1 deploy   deploy    812 Sep 28 16:02 ${g}${B}deploy.sh${z}"
                echo "lrwxrwxrwx 1 deploy   deploy     21 Sep 28 16:02 ${c}${B}current${z} -> releases/2026.10.01"
                p web-01 '~' 'tail -n 6 /var/log/nginx/access.log'
                for path in / /about /assets/app.css /assets/logo.svg /api/health /; do
                  printf '203.0.113.%-3s - - [02/Oct/2026:14:31:5%s +0000] "GET %-17s HTTP/2.0" %s200 OK%s 5312 "-" "Mozilla/5.0"\n' $((RANDOM % 200)) $((RANDOM % 10)) "$path" "$g" "$z"
                done
                p web-01 '~' 'df -h /'
                echo "Filesystem      Size  Used Avail Use% Mounted on"
                echo "/dev/nvme0n1p1   80G   31G   49G  39% /"
                """,
            ["node"] = prelude + """

                p "$DEMO_HOST" '~' 'sudo ./deploy.sh 2026.10.02'
                echo "${c}→${z} Fetching release ${B}2026.10.02${z}"
                echo "${c}→${z} Verifying checksum ... ${g}ok${z}"
                echo "${c}→${z} Unpacking to releases/2026.10.02"
                echo "${c}→${z} Running migrations ... ${g}none pending${z}"
                echo "${c}→${z} Switching symlink: current -> releases/2026.10.02"
                echo "${c}→${z} Reloading php8.3-fpm ... ${g}ok${z}"
                echo "${c}→${z} Reloading nginx ... ${g}ok${z}"
                echo "${c}→${z} Health check http://localhost/api/health ... ${g}${B}200 OK${z}"
                echo
                echo "${g}${B}✓ deploy complete${z} in 14.2s"
                """,
            ["build"] = prelude + """

                p "$DEMO_HOST" '~/app' 'make release'
                echo "${b}[1/6]${z} Restoring packages ... ${g}done${z}"
                echo "${b}[2/6]${z} Compiling src/core ... ${g}done${z} ${d}(42 files)${z}"
                echo "${b}[3/6]${z} Compiling src/app ... ${g}done${z} ${d}(117 files)${z}"
                echo "${b}[4/6]${z} Running tests ... ${g}412 passed${z}, ${y}3 skipped${z}"
                echo "${b}[5/6]${z} Packaging ${m}app-2026.10.02.tar.gz${z}"
                echo "${b}[6/6]${z} Uploading to ${c}s3://release-artifacts/${z}"
                echo "      ${y}warning:${z} artifact is 18% larger than the previous release"
                echo "      ${r}error:${z} none"
                echo
                echo "${g}${B}Build succeeded${z} in 3m 07s"
                """,
        };
        var paths = new Dictionary<string, string>();
        foreach (var (key, body) in scripts)
        {
            var path = Path.Combine(dir, key + ".sh");
            File.WriteAllText(path, body);
            paths[key] = path;
        }
        return paths;
    }

    /// <summary>A demo "home" for the local pane, and a demo web root on the SFTP server.</summary>
    private static string CreateDemoFolders(string sftpRoot)
    {
        var home = Path.Combine(Path.GetTempPath(), "demo");
        if (Directory.Exists(home))
            Directory.Delete(home, true);
        var site = Path.Combine(home, "website");
        Directory.CreateDirectory(Path.Combine(site, "assets", "img"));
        File.WriteAllText(Path.Combine(site, "index.html"), new string('x', 9832));
        File.WriteAllText(Path.Combine(site, "about.html"), new string('x', 6214));
        File.WriteAllText(Path.Combine(site, "assets", "app.css"), new string('x', 48_211));
        File.WriteAllText(Path.Combine(site, "assets", "app.js"), new string('x', 183_402));
        File.WriteAllBytes(Path.Combine(site, "assets", "img", "hero.jpg"), new byte[412_880]);
        File.WriteAllBytes(Path.Combine(site, "assets", "img", "logo.svg"), new byte[3_120]);
        File.WriteAllText(Path.Combine(site, "robots.txt"), "User-agent: *\nAllow: /\n");
        var releases = Path.Combine(home, "releases");
        Directory.CreateDirectory(releases);
        File.WriteAllBytes(Path.Combine(releases, "app-2026.10.02.tar.gz"), new byte[7_340_032]);
        File.WriteAllText(Path.Combine(releases, "app-2026.10.02.sha256"), new string('f', 64) + "  app-2026.10.02.tar.gz\n");
        File.WriteAllText(Path.Combine(releases, "CHANGELOG.md"), "# 2026.10.02\n");

        var www = Path.Combine(sftpRoot, "var", "www", "example.com");
        Directory.CreateDirectory(Path.Combine(www, "releases"));
        File.WriteAllText(Path.Combine(www, "deploy.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(www, ".env"), "APP_ENV=production\n");
        return home;
    }
}
