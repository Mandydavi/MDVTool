using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MDVPack
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var pack = Package.Read();
                if (pack == null)
                {
                    if (Has(args, "verify"))
                    {
                        Console.WriteLine("NOT_A_PACKAGE");
                        return 1;
                    }
                    MessageBox.Show("这是打包器模板，不是发布包。\n请运行 pack.bat 生成发布包。",
                        "MDV 打包器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                if (Has(args, "verify"))
                {
                    Console.WriteLine("OK mode=" + pack.Mode +
                                      " version=" + pack.Version +
                                      " payload=" + new FileInfo(pack.Zip).Length);
                    return 0;
                }

                if (string.Equals(pack.Mode, "setup", StringComparison.OrdinalIgnoreCase))
                {
                    Application.Run(new SetupWizard(pack));
                    return 0;
                }
                return Sfx.Run(pack);
            }
            catch (Exception ex)
            {
                if (Has(args, "verify")) Console.WriteLine("ERROR " + ex.Message);
                else MessageBox.Show("展开失败：" + ex.Message, "MDV 打包器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static bool Has(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a.TrimStart('/', '-'), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// 读取附加在 exe 尾部的 payload：
    /// [stub][payload.zip][config.json][magic 8B][payloadLen 8B][configLen 8B]
    /// 往 PE 尾部追加数据不影响 Windows 加载器，因此一个模板能拼出多种形态的发布包。
    /// </summary>
    internal sealed class Package
    {
        public string Mode = "sfx";
        public string Name = "MDV Tool";
        public string Version = "";
        public string Exe = "MDV Tool.exe";
        public string Id = "MDVTool";
        public string Zip = "";

        public static Package Read()
        {
            string own = Application.ExecutablePath;
            using (var fs = new FileStream(own, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 64) return null;
                fs.Seek(-24, SeekOrigin.End);
                var tail = new byte[24];
                if (fs.Read(tail, 0, 24) != 24) return null;
                if (Encoding.ASCII.GetString(tail, 0, 8) != "MDVPACK1") return null;
                long payloadLen = BitConverter.ToInt64(tail, 8);
                long configLen = BitConverter.ToInt64(tail, 16);
                if (payloadLen <= 0 || configLen < 0 || payloadLen + configLen + 24 > fs.Length) return null;

                long configStart = fs.Length - 24 - configLen;
                long payloadStart = configStart - payloadLen;

                var cfgBytes = new byte[(int)configLen];
                fs.Position = configStart;
                if (fs.Read(cfgBytes, 0, cfgBytes.Length) != cfgBytes.Length) return null;
                string cfg = Encoding.UTF8.GetString(cfgBytes);

                var p = new Package
                {
                    Mode = Pick(cfg, "mode", "sfx"),
                    Name = Pick(cfg, "name", "MDV Tool"),
                    Version = Pick(cfg, "version", ""),
                    Exe = Pick(cfg, "exe", "MDV Tool.exe"),
                    Id = Pick(cfg, "id", "MDVTool"),
                };

                string safeVer = Regex.Replace(p.Version, "[^0-9A-Za-z.-]", "_");
                string zip = Path.Combine(Path.GetTempPath(),
                    "mdvpack-" + p.Id + "-" + safeVer + "-" + (payloadLen % 1000000) + ".zip");
                using (var outFs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Position = payloadStart;
                    var buf = new byte[1048576];
                    long left = payloadLen;
                    while (left > 0)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) break;
                        outFs.Write(buf, 0, n);
                        left -= n;
                    }
                }
                p.Zip = zip;
                return p;
            }
        }

        private static string Pick(string json, string key, string fallback)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        public void ExtractTo(string dir, Action<int, string> progress)
        {
            Directory.CreateDirectory(dir);
            using (var za = ZipFile.OpenRead(Zip))
            {
                int total = Math.Max(1, za.Entries.Count);
                int i = 0;
                foreach (var e in za.Entries)
                {
                    i++;
                    string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string dest = Path.Combine(dir, rel);
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    try { e.ExtractToFile(dest, true); }
                    catch (IOException) { }
                    if (progress != null) progress(i * 100 / total, rel);
                }
            }
        }
    }

    /// <summary>单文件自解压：双击后展开到用户目录并直接运行，接收方只看到一个 exe。</summary>
    internal static class Sfx
    {
        public static int Run(Package pack)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDV", pack.Id, string.IsNullOrEmpty(pack.Version) ? "current" : pack.Version);
            string marker = Path.Combine(root, ".mdv-ready");
            if (!File.Exists(marker))
            {
                pack.ExtractTo(root, null);
                File.WriteAllText(marker, pack.Version);
            }
            string exe = Path.Combine(root, pack.Exe);
            if (!File.Exists(exe)) throw new Exception("发布包里缺少主程序：" + pack.Exe);
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = root,
            });
            return 0;
        }
    }

    /// <summary>
    /// NSIS / Inno Setup 风格多步安装向导：左侧品牌竖条 + 右侧分页内容 + 底部按钮栏。
    /// 步骤：欢迎 → 自述 → 安装位置 → 附加任务 → 正在安装 → 完成。
    /// </summary>
    internal sealed class SetupWizard : Form
    {
        private readonly Package _pack;
        private readonly Panel[] _pages = new Panel[6];
        private readonly Button _back = new Button();
        private readonly Button _next = new Button();
        private readonly Button _cancel = new Button();
        private readonly TextBox _dir = new TextBox();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly CheckBox _startMenu = new CheckBox();
        private readonly CheckBox _run = new CheckBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _progressText = new Label();
        private readonly Label _doneText = new Label();
        private int _step;
        private bool _installing;

        public SetupWizard(Package pack)
        {
            _pack = pack;
            Text = pack.Name + " 安装";
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(580, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9f);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildSide();
            BuildPages();
            BuildFooter();
            ShowStep(0);
        }

        private void BuildSide()
        {
            var side = new Panel
            {
                Dock = DockStyle.Left,
                Width = 180,
                BackColor = Color.FromArgb(0, 0, 128),
            };
            var icon = new Panel
            {
                BackColor = Color.White,
                Size = new Size(68, 68),
                Location = new Point(48, 42),
                BorderStyle = BorderStyle.FixedSingle,
            };
            var mark = new Label
            {
                AutoSize = false,
                Text = "MDV",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 0, 128),
            };
            icon.Controls.Add(mark);
            side.Controls.Add(icon);
            side.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "MDV Tool",
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 11f, FontStyle.Bold),
                Location = new Point(0, 128),
                Size = new Size(180, 24),
                TextAlign = ContentAlignment.MiddleCenter,
            });
            side.Controls.Add(new Label
            {
                AutoSize = false,
                Text = string.IsNullOrEmpty(_pack.Version) ? "" : "v" + _pack.Version,
                ForeColor = Color.FromArgb(190, 200, 255),
                Location = new Point(0, 152),
                Size = new Size(180, 20),
                TextAlign = ContentAlignment.MiddleCenter,
            });
            side.Controls.Add(new Label
            {
                AutoSize = false,
                Text = "Offline full package",
                ForeColor = Color.FromArgb(170, 180, 240),
                Font = new Font("Microsoft YaHei", 8f),
                Location = new Point(0, 320),
                Size = new Size(180, 20),
                TextAlign = ContentAlignment.MiddleCenter,
            });
            Controls.Add(side);
        }

        private void BuildFooter()
        {
            var foot = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                BackColor = SystemColors.Control,
            };
            foot.Controls.Add(new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = Color.FromArgb(210, 210, 210),
            });

            _back.Text = "< 上一步(B)";
            _back.SetBounds(224, 13, 88, 27);
            _back.Click += (s, e) => { if (_step > 0 && !_installing) ShowStep(_step - 1); };
            foot.Controls.Add(_back);

            _next.Text = "下一步(N) >";
            _next.SetBounds(318, 13, 88, 27);
            _next.Click += Next_Click;
            foot.Controls.Add(_next);

            _cancel.Text = "取消(C)";
            _cancel.SetBounds(412, 13, 88, 27);
            _cancel.Click += (s, e) => Close();
            foot.Controls.Add(_cancel);

            Controls.Add(foot);
        }

        private Panel NewPage()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
            Controls.Add(p);
            p.BringToFront();
            return p;
        }

        private static Label Title(string text, Panel page)
        {
            var l = new Label
            {
                AutoSize = false,
                Text = text,
                Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold),
                Location = new Point(25, 22),
                Size = new Size(350, 44),
            };
            page.Controls.Add(l);
            return l;
        }

        private static Label Body(string text, Panel page, int top, int height)
        {
            var l = new Label
            {
                AutoSize = false,
                Text = text,
                Location = new Point(25, top),
                Size = new Size(350, height),
                Font = new Font("Microsoft YaHei", 9f),
            };
            page.Controls.Add(l);
            return l;
        }

        private void BuildPages()
        {
            for (int i = 0; i < _pages.Length; i++) _pages[i] = NewPage();

            Title("欢迎使用 MDV Tool 安装程序", _pages[0]);
            Body("此程序将引导你完成 MDV Tool 的安装。\n\n" +
                 "在安装之前，请先关闭其他所有应用程序。这将确保安装程序能够更新" +
                 "所需的系统文件，从而避免在安装后重新启动计算机。\n\n" +
                 "这是完整离线包：主程序与下载组件都在里面，装完不用联网、不用配环境。\n\n" +
                 "点击 [下一步(N)] 继续。", _pages[0], 66, 230);

            Title("自述信息", _pages[1]);
            Body("MDV Tool " + (string.IsNullOrEmpty(_pack.Version) ? "" : "v" + _pack.Version) + "\n\n" +
                 "包含内容：\n" +
                 "  · 主程序（自包含，无需 .NET 运行时）\n" +
                 "  · 下载组件（离线可用，不会联网补下）\n" +
                 "  · 浏览器嗅探扩展\n" +
                 "  · 在线安装器与分发说明\n\n" +
                 "安装完成后可在「设置 → 应用」中卸载。", _pages[1], 66, 230);

            Title("选择安装位置", _pages[2]);
            Body("安装 MDV Tool 到：", _pages[2], 66, 22);
            _dir.SetBounds(25, 94, 250, 24);
            _dir.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDV Tool");
            _pages[2].Controls.Add(_dir);
            var browse = new Button { Text = "浏览(R)...", Location = new Point(282, 93), Size = new Size(78, 26) };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = _dir.Text })
                    if (d.ShowDialog() == DialogResult.OK) _dir.Text = d.SelectedPath;
            };
            _pages[2].Controls.Add(browse);
            Body("所需空间：约 80 MB（完整离线包）\n" +
                 "建议安装在非系统盘或用户目录，方便以后直接覆盖更新。", _pages[2], 132, 60);

            Title("选择附加任务", _pages[3]);
            Body("请选择安装时要执行的附加任务：", _pages[3], 66, 22);
            _desktop.Text = "创建桌面快捷方式";
            _desktop.Checked = true;
            _desktop.SetBounds(25, 100, 330, 24);
            _pages[3].Controls.Add(_desktop);
            _startMenu.Text = "创建开始菜单快捷方式与卸载入口";
            _startMenu.Checked = true;
            _startMenu.SetBounds(25, 130, 330, 24);
            _pages[3].Controls.Add(_startMenu);
            _run.Text = "安装完成后运行 MDV Tool";
            _run.Checked = true;
            _run.SetBounds(25, 160, 330, 24);
            _pages[3].Controls.Add(_run);

            Title("正在安装", _pages[4]);
            Body("正在安装 MDV Tool，请稍候 …", _pages[4], 66, 24);
            _bar.SetBounds(25, 106, 340, 20);
            _pages[4].Controls.Add(_bar);
            _progressText.AutoSize = false;
            _progressText.SetBounds(25, 134, 340, 44);
            _progressText.ForeColor = Color.Gray;
            _pages[4].Controls.Add(_progressText);

            Title("MDV Tool 安装完成", _pages[5]);
            _doneText.AutoSize = false;
            _doneText.SetBounds(25, 66, 340, 110);
            _pages[5].Controls.Add(_doneText);
            _pages[5].Controls.Add(new Label
            {
                AutoSize = false,
                Text = "点击 [完成(F)] 退出安装程序。",
                Location = new Point(25, 190),
                Size = new Size(340, 24),
            });
        }

        private void ShowStep(int n)
        {
            _step = n;
            for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == n;
            _back.Enabled = n > 0 && n < 5 && !_installing;
            _cancel.Enabled = !_installing && n < 5;
            _next.Enabled = !_installing;
            _next.Text = n == 3 ? "安装(I)" : (n == 5 ? "完成(F)" : "下一步(N) >");
        }

        private async void Next_Click(object sender, EventArgs e)
        {
            if (_step == 5) { FinishAndClose(); return; }
            if (_step < 3) { ShowStep(_step + 1); return; }
            if (_step != 3 || _installing) return;

            string dir = _dir.Text.Trim();
            if (string.IsNullOrEmpty(dir)) { MessageBox.Show(this, "请选择安装目录。"); return; }

            _installing = true;
            ShowStep(4);
            _next.Enabled = false;
            _back.Enabled = false;
            _cancel.Enabled = false;
            _bar.Value = 0;

            try
            {
                await Task.Run(() => _pack.ExtractTo(dir, (p, f) =>
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _bar.Value = Math.Min(100, p);
                            _progressText.Text = f.Length > 48 ? f.Substring(0, 48) : f;
                        }));
                    }
                    catch { }
                }));

                string exe = Path.Combine(dir, _pack.Exe);
                if (!File.Exists(exe)) throw new Exception("发布包里缺少主程序：" + _pack.Exe);

                _progressText.Text = "正在创建快捷方式与卸载入口 …";
                Shortcuts.Create(dir, exe, _pack.Name, _pack.Version,
                    _desktop.Checked, _startMenu.Checked);
                _bar.Value = 100;

                _doneText.Text = "MDV Tool 已安装到：\n" + dir + "\n\n" +
                                 "下载组件已随包分发，首次运行不需要联网。";
                _installing = false;
                ShowStep(5);
            }
            catch (Exception ex)
            {
                _installing = false;
                _progressText.Text = "失败：" + ex.Message;
                MessageBox.Show(this, ex.Message, "安装失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowStep(3);
            }
        }

        private void FinishAndClose()
        {
            if (_run.Checked && !string.IsNullOrWhiteSpace(_dir.Text))
            {
                string exe = Path.Combine(_dir.Text.Trim(), _pack.Exe);
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = _dir.Text.Trim(),
                    });
            }
            Close();
        }
    }

    internal static class Shortcuts
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MDVTool";

        public static void Create(string dir, string exe, string name, string version,
            bool desktop, bool startMenu)
        {
            try
            {
                if (startMenu)
                {
                    string group = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs), name);
                    Directory.CreateDirectory(group);
                    Make(Path.Combine(group, name + ".lnk"), exe);
                }
                if (desktop)
                    Make(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        name + ".lnk"), exe);
            }
            catch { }

            if (!startMenu) return;
            try
            {
                string un = Path.Combine(dir, "uninstall.bat");
                File.WriteAllText(un,
                    "@echo off\r\n" +
                    "taskkill /IM \"MDV Tool.exe\" /F >nul 2>&1\r\n" +
                    "reg delete \"HKCU\\" + Key + "\" /f >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.exe\" >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.dll\" >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.json\" >nul 2>&1\r\n" +
                    "del /Q \"%~dp0*.md\" >nul 2>&1\r\n" +
                    "rmdir /S /Q \"%~dp0BrowserExtension\" >nul 2>&1\r\n" +
                    "rmdir \"%~dp0\" 2>nul\r\n" +
                    "echo Removed.\r\npause\r\n", Encoding.ASCII);

                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    k.SetValue("DisplayName", name);
                    k.SetValue("DisplayVersion", version);
                    k.SetValue("InstallLocation", dir);
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("UninstallString", "\"" + un + "\"");
                    k.SetValue("NoModify", 1);
                    k.SetValue("NoRepair", 1);
                }
            }
            catch { }
        }

        private static void Make(string lnk, string target)
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
    }
}
