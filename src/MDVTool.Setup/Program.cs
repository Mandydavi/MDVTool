using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MDVSetup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 同时接受 /silent 和 -silent（Git Bash / MSYS 会把 /xxx 当路径改写）
            if (args != null && Array.Exists(args,
                a => string.Equals(a.TrimStart('/', '-'), "silent", StringComparison.OrdinalIgnoreCase)))
                return Silent.Run(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }
    }

    /// <summary>
    /// 命令行静默安装：MDVSetup.exe /silent [/url=...] [/dir=...] [/out=...]
    ///   /downloadOnly  只下载不安装   /noShortcut 不建快捷方式   /noRegister 不写卸载注册表
    /// </summary>
    internal static class Silent
    {
        public static int Run(string[] args)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            var cfg = SetupConfig.Load(Path.Combine(here, "setup.json"));
            string url = Arg(args, "url");
            if (string.IsNullOrEmpty(url)) url = cfg.Url;
            string dir = Arg(args, "dir");
            if (string.IsNullOrEmpty(dir)) dir = Environment.ExpandEnvironmentVariables(cfg.DefaultDir);
            string outFile = Arg(args, "out");
            Action<string> log = s => Console.WriteLine(s);

            try
            {
                string staged;
                if (string.IsNullOrEmpty(url))
                {
                    staged = Path.Combine(here, cfg.LocalFile);
                    if (!File.Exists(staged)) throw new Exception("没找到本地主程序：" + staged);
                    log("OFFLINE " + staged);
                }
                else
                {
                    string work = string.IsNullOrEmpty(outFile)
                        ? Downloader.WorkDirFor(url) : Path.GetDirectoryName(Path.GetFullPath(outFile));
                    Directory.CreateDirectory(work);
                    staged = string.IsNullOrEmpty(outFile)
                        ? Path.Combine(work, cfg.ExeName) : Path.GetFullPath(outFile);
                    log("DOWNLOAD " + url);
                    Downloader.DownloadAsync(url, staged, CancellationToken.None,
                        (d, t, b) => { }, log).GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(cfg.Sha256) &&
                        !string.Equals(Downloader.Sha256Of(staged), cfg.Sha256.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        throw new Exception("SHA-256 校验不通过");
                    log("DOWNLOAD_OK " + new FileInfo(staged).Length);
                }

                if (Has(args, "downloadOnly")) return 0;

                Installer.Install(staged, cfg, dir,
                    !Has(args, "noShortcut"), false, !Has(args, "noRegister"), log);
                log("INSTALL_OK " + Path.Combine(dir, cfg.ExeName));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR " + ex.Message);
                return 1;
            }
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string a in args)
                if (string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Arg(string[] args, string name)
        {
            foreach (string a in args)
            {
                string s = a.TrimStart('/', '-');
                int i = s.IndexOf('=');
                if (i > 0 && string.Equals(s.Substring(0, i), name, StringComparison.OrdinalIgnoreCase))
                    return s.Substring(i + 1).Trim('"');
            }
            return "";
        }
    }

    internal sealed class SetupConfig
    {
        public string AppName = "MDV Tool";
        public string ExeName = "MDV Tool.exe";
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
        public string LocalFile = "MDV Tool.exe";
        public string DefaultDir = @"%LOCALAPPDATA%\MDVTool";

        public static SetupConfig Load(string path)
        {
            var c = new SetupConfig();
            try
            {
                string s = File.ReadAllText(path, Encoding.UTF8);
                c.AppName = Pick(s, "appName", c.AppName);
                c.ExeName = Pick(s, "exeName", c.ExeName);
                c.Version = Pick(s, "version", c.Version);
                c.Url = Pick(s, "url", c.Url);
                c.Sha256 = Pick(s, "sha256", c.Sha256);
                c.LocalFile = Pick(s, "localFile", c.LocalFile);
                c.DefaultDir = Pick(s, "defaultDir", c.DefaultDir);
            }
            catch { }
            return c;
        }

        private static string Pick(string json, string key, string fallback)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return fallback;
            return m.Groups[1].Value
                .Replace("\\n", "\n")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }
    }

    internal static class Installer
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MDVTool";

        public static void Install(string sourceExe, SetupConfig cfg, string dir,
            bool shortcuts, bool desktop, bool register, Action<string> log)
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, cfg.ExeName);
            if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceExe, target, true);
            log("已安装到 " + target);

            if (shortcuts)
            {
                string group = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), cfg.AppName);
                try
                {
                    Directory.CreateDirectory(group);
                    Shortcut(Path.Combine(group, cfg.AppName + ".lnk"), target);
                    if (desktop)
                    {
                        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                        Shortcut(Path.Combine(desk, cfg.AppName + ".lnk"), target);
                    }
                    log("快捷方式已创建");
                }
                catch (Exception ex) { log("创建快捷方式失败（不影响使用）：" + ex.Message); }
            }

            if (!register) return;

            string uninstall = Path.Combine(dir, "uninstall.bat");
            try
            {
                File.WriteAllText(uninstall,
                    "@echo off\r\n" +
                    "taskkill /IM \"MDV Tool.exe\" /F >nul 2>&1\r\n" +
                    "reg delete \"HKCU\\" + UninstallKey + "\" /f >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.exe\" >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.json\" >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.lnk\" >nul 2>&1\r\n" +
                    "rmdir \"%~dp0\" 2>nul\r\n" +
                    "echo MDV Tool has been removed.\r\n" +
                    "pause\r\n", Encoding.ASCII);

                using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", cfg.AppName);
                    key.SetValue("DisplayVersion", cfg.Version);
                    key.SetValue("Publisher", "Mandydavi");
                    key.SetValue("InstallLocation", dir);
                    key.SetValue("DisplayIcon", target);
                    key.SetValue("UninstallString", "\"" + uninstall + "\"");
                    key.SetValue("NoModify", 1);
                    key.SetValue("NoRepair", 1);
                }
                log("卸载入口已写入（设置 → 应用 → 已安装的应用）");
            }
            catch (Exception ex) { log("写卸载信息失败（不影响使用）：" + ex.Message); }
        }

        private static void Shortcut(string lnk, string target)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { Path.GetDirectoryName(target) });
            st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty,
                null, sc, new object[] { target + ",0" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
        }

        public static async Task RunWingetAsync(string id, Action<string> log, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("winget",
                "install --id " + id + " -e --silent --accept-package-agreements " +
                "--accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                string so = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string se = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
                p.WaitForExit();
                ct.ThrowIfCancellationRequested();
                string text = (so + "\n" + se).Trim();
                if (text.Length > 0)
                {
                    foreach (string line in text.Split('\n'))
                        if (line.Trim().Length > 0 && line.Length < 300) log("    " + line.Trim());
                }
                log(p.ExitCode == 0 ? "完成：" + id : "winget 返回 " + p.ExitCode + "：" + id);
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private static readonly string[] AppNames =
        {
            "Visual Studio Code", "7-Zip", "Google Chrome", "Git",
            "FFmpeg", "yt-dlp", "aria2", "Everything",
            "Node.js LTS", "Python 3", "Notepad++", "PotPlayer",
            "PowerToys", "Windows Terminal", "VLC", "Discord"
        };

        private static readonly string[] AppIds =
        {
            "Microsoft.VisualStudioCode", "7zip.7zip", "Google.Chrome", "Git.Git",
            "Gyan.FFmpeg", "yt-dlp.yt-dlp", "aria2.aria2", "voidtools.Everything",
            "OpenJS.NodeJS.LTS", "Python.Python.3.12", "Notepad++.Notepad++", "Daum.PotPlayer",
            "Microsoft.PowerToys", "Microsoft.WindowsTerminal", "VideoLAN.VLC", "Discord.Discord"
        };

        private readonly TextBox _url = new TextBox();
        private readonly TextBox _dir = new TextBox();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly CheckBox _run = new CheckBox();
        private readonly CheckBox _asInstaller = new CheckBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly Label _speed = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly Button _start = new Button();
        private readonly Button _cancel = new Button();
        private readonly CheckedListBox _apps = new CheckedListBox();
        private readonly Button _appsBtn = new Button();

        private CancellationTokenSource _cts;
        private bool _busy;

        public SetupForm()
        {
            Text = "MDV 安装器";
            Size = new Size(680, 665);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9f);

            int x = 18, w = 628;
            var title = new Label
            {
                Text = "MDV 安装器 · 下载并安装软件",
                Location = new Point(x, 14), Size = new Size(w, 28),
                Font = new Font("Microsoft YaHei", 12f, FontStyle.Bold)
            };
            Controls.Add(title);

            Controls.Add(Label("下载地址（留空则用安装器同目录的主程序离线安装）", x, 50, w));
            _url.SetBounds(x, 70, w, 24);
            Controls.Add(_url);

            Controls.Add(Label("安装到", x, 102, w));
            _dir.SetBounds(x, 122, 512, 24);
            Controls.Add(_dir);
            var browse = new Button { Text = "浏览…", Location = new Point(538, 121), Size = new Size(108, 26) };
            browse.Click += Browse_Click;
            Controls.Add(browse);

            _desktop.Text = "创建桌面快捷方式";
            _desktop.Checked = true;
            _desktop.SetBounds(x, 156, 180, 24);
            Controls.Add(_desktop);

            _run.Text = "完成后运行";
            _run.Checked = true;
            _run.SetBounds(204, 156, 130, 24);
            Controls.Add(_run);

            _asInstaller.Text = "这是安装包（下载后直接运行）";
            _asInstaller.SetBounds(340, 156, 300, 24);
            Controls.Add(_asInstaller);

            _start.Text = "开始安装";
            _start.SetBounds(x, 190, 110, 30);
            _start.Click += Start_Click;
            Controls.Add(_start);

            _cancel.Text = "取消";
            _cancel.SetBounds(136, 190, 90, 30);
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); };
            Controls.Add(_cancel);

            _bar.SetBounds(x, 232, w, 18);
            _bar.Maximum = 100;
            Controls.Add(_bar);

            _status.SetBounds(x, 256, 360, 20);
            _status.Text = "就绪";
            Controls.Add(_status);
            _speed.SetBounds(388, 256, 258, 20);
            _speed.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_speed);

            _log.SetBounds(x, 282, w, 168);
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = Color.White;
            Controls.Add(_log);

            Controls.Add(Label("常用软件 · 勾选后由 winget 自动下载并静默安装", x, 460, w));
            _apps.SetBounds(x, 482, 500, 100);
            _apps.CheckOnClick = true;
            _apps.MultiColumn = true;
            _apps.ColumnWidth = 162;
            _apps.Items.AddRange(AppNames);
            Controls.Add(_apps);

            _appsBtn.Text = "安装所选软件";
            _appsBtn.SetBounds(526, 482, 120, 32);
            _appsBtn.Click += AppsBtn_Click;
            Controls.Add(_appsBtn);

            var cfg = SetupConfig.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setup.json"));
            _url.Text = cfg.Url;
            _dir.Text = Environment.ExpandEnvironmentVariables(cfg.DefaultDir);
            Log("MDV 安装器就绪" + (string.IsNullOrEmpty(cfg.Version) ? "" : " · v" + cfg.Version));
            Log("下载地址留空 = 离线安装同目录的 " + cfg.LocalFile);
            Log("下载组件（引擎）由主程序首次启动时自动获取，无需打包。");
        }

        private static Label Label(string text, int x, int y, int w)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, 20) };
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog { SelectedPath = _dir.Text })
            {
                if (dlg.ShowDialog() == DialogResult.OK) _dir.Text = dlg.SelectedPath;
            }
        }

        private async void Start_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _start.Enabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                await InstallAsync(_cts.Token);
            }
            catch (OperationCanceledException) { Log("已取消（未完成的下载会保留，下次继续）"); }
            catch (Exception ex)
            {
                Log("失败：" + ex.Message);
                MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                _start.Enabled = true;
            }
        }

        private async Task InstallAsync(CancellationToken ct)
        {
            string here = AppDomain.CurrentDomain.BaseDirectory;
            var cfg = SetupConfig.Load(Path.Combine(here, "setup.json"));
            string url = _url.Text.Trim();
            if (string.IsNullOrEmpty(url)) url = cfg.Url;

            string dir = Environment.ExpandEnvironmentVariables(_dir.Text.Trim());
            if (string.IsNullOrWhiteSpace(dir)) throw new Exception("请先选择安装目录");

            string staged;
            if (string.IsNullOrEmpty(url))
            {
                staged = Path.Combine(here, cfg.LocalFile);
                if (!File.Exists(staged))
                    throw new Exception("没填下载地址，同目录也没找到 " + cfg.LocalFile +
                        "。把主程序放到安装器旁边，或在 setup.json 里填 url。");
                Log("离线安装：" + staged);
            }
            else
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                    !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    throw new Exception("下载地址不是有效的 http/https 链接");

                string work = Downloader.WorkDirFor(url);
                staged = Path.Combine(work, cfg.ExeName);
                Log("开始下载：" + url);
                await Downloader.DownloadAsync(url, staged, ct, OnProgress, Log);
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrEmpty(cfg.Sha256))
                {
                    string actual = Downloader.Sha256Of(staged);
                    if (!string.Equals(actual, cfg.Sha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                        throw new Exception("SHA-256 校验不通过，文件可能被篡改：\n期望 " + cfg.Sha256 + "\n实际 " + actual);
                    Log("SHA-256 校验通过");
                }
            }

            if (_asInstaller.Checked)
            {
                Log("下载完成，正在运行安装程序 …");
                Process.Start(new ProcessStartInfo(staged) { UseShellExecute = true });
                Log("已交给安装程序，按它的提示完成即可。");
                return;
            }

            Installer.Install(staged, cfg, dir, true, _desktop.Checked, true, Log);
            _bar.Value = 100;
            _status.Text = "安装完成";
            Log("完成。首次启动主程序时会自动补齐下载组件。");
            if (_run.Checked)
            {
                string exe = Path.Combine(dir, cfg.ExeName);
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }

        private async void AppsBtn_Click(object sender, EventArgs e)
        {
            if (_busy) return;
            int count = 0;
            for (int i = 0; i < _apps.Items.Count; i++) if (_apps.GetItemChecked(i)) count++;
            if (count == 0)
            {
                MessageBox.Show(this, "先勾选要安装的软件。", "提示");
                return;
            }

            _busy = true;
            _appsBtn.Enabled = false;
            _cts = new CancellationTokenSource();
            try
            {
                for (int i = 0; i < _apps.Items.Count; i++)
                {
                    if (!_apps.GetItemChecked(i)) continue;
                    _cts.Token.ThrowIfCancellationRequested();
                    Log("winget 安装 " + AppIds[i] + " …");
                    await Installer.RunWingetAsync(AppIds[i], Log, _cts.Token);
                }
                Log("所选软件处理完毕。");
            }
            catch (Win32Exception)
            {
                Log("没找到 winget。请从 Microsoft Store 安装「应用安装程序（App Installer）」后重试。");
            }
            catch (OperationCanceledException) { Log("已取消"); }
            catch (Exception ex) { Log("安装出错：" + ex.Message); }
            finally
            {
                _busy = false;
                _appsBtn.Enabled = true;
            }
        }

        private void OnProgress(long done, long total, double bps)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => OnProgress(done, total, bps)));
                return;
            }
            if (total > 0) _bar.Value = (int)Math.Min(100, done * 100 / total);
            _status.Text = "已下载 " + Fmt(done) + (total > 0 ? " / " + Fmt(total) : "");
            _speed.Text = Fmt(bps) + "/s";
        }

        private void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(s))); return; }
            _log.AppendText(s + Environment.NewLine);
        }

        private static string Fmt(double n)
        {
            if (n >= 1073741824d) return (n / 1073741824d).ToString("0.00") + " GB";
            if (n >= 1048576d) return (n / 1048576d).ToString("0.0") + " MB";
            if (n >= 1024d) return (n / 1024d).ToString("0") + " KB";
            return n.ToString("0") + " B";
        }
    }
}
