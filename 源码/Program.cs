using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclDownloadFix
{
    internal static class Program
    {
        internal const string PclExeName = "Plain Craft Launcher 2.exe";
        internal const string AppTitle = "PCL 下载修复器";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            Application.Run(new MainForm(args));
        }

        private static System.Reflection.Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            var name = new System.Reflection.AssemblyName(e.Name).Name;
            if (name != "dnlib") return null;
            using (var s = typeof(Program).Assembly.GetManifestResourceStream("dnlib.dll"))
            {
                if (s == null) return null;
                var buf = new byte[s.Length];
                s.Read(buf, 0, buf.Length);
                return System.Reflection.Assembly.Load(buf);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _pathBox = new TextBox();
        private readonly Button _browse = new Button();
        private readonly Button _start = new Button();
        private readonly Label _status = new Label();
        private readonly CheckBox _remember = new CheckBox();
        private readonly Label _hint = new Label();

        public MainForm(string[] args)
        {
            Text = Program.AppTitle;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 232);
            Font = new Font("Microsoft YaHei UI", 9f);
            Icon = SystemIcons.Application;

            var title = new Label
            {
                Text = "PCL 下载修复器",
                Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 14)
            };
            var sub = new Label
            {
                Text = "让 PCL 2.99 的「下载 → 原版游戏」重新可用（不修改 PCL 任何文件）",
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90),
                Location = new Point(20, 46)
            };

            var lab = new Label { Text = "PCL 路径：", AutoSize = true, Location = new Point(20, 84) };
            _pathBox.Location = new Point(92, 81);
            _pathBox.Size = new Size(352, 25);
            _browse.Text = "浏览…";
            _browse.Location = new Point(452, 80);
            _browse.Size = new Size(88, 27);
            _browse.Click += (s, e) => BrowsePcl();

            _remember.Text = "记住此路径";
            _remember.AutoSize = true;
            _remember.Location = new Point(92, 112);
            _remember.CheckedChanged += (s, e) => SaveSettings();

            _start.Text = "启动 PCL";
            _start.Location = new Point(392, 108);
            _start.Size = new Size(148, 34);
            _start.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _start.Click += (s, e) => StartPcl();

            _status.Location = new Point(20, 152);
            _status.Size = new Size(520, 22);
            _status.ForeColor = Color.FromArgb(0, 110, 190);

            _hint.Location = new Point(20, 176);
            _hint.Size = new Size(524, 48);
            _hint.ForeColor = Color.FromArgb(120, 120, 120);
            _hint.Text = "说明：PCL 2.99 删除了下载页的版本列表加载，本程序在启动 PCL 时于内存中补回，\r\n" +
                         "不动 PCL 文件；PCL 更新后依旧有效。整合包安装功能被整体删除，暂无法恢复。";

            Controls.AddRange(new Control[] { title, sub, lab, _pathBox, _browse, _remember, _start, _status, _hint });

            LoadSettings();
            if (args != null && args.Length > 0 && File.Exists(args[0])) _pathBox.Text = args[0];
            if (string.IsNullOrEmpty(_pathBox.Text) || !File.Exists(_pathBox.Text)) _pathBox.Text = AutoLocate() ?? "";
        }

        private string SettingsPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "PCL下载修复.ini"); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (var line in File.ReadAllLines(SettingsPath, Encoding.UTF8))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line.Substring(0, i).Trim();
                    var v = line.Substring(i + 1).Trim();
                    if (k == "PclPath") _pathBox.Text = v;
                    else if (k == "Remember") _remember.Checked = v == "1";
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[]
                {
                    "PclPath=" + _pathBox.Text,
                    "Remember=" + (_remember.Checked ? "1" : "0")
                }, Encoding.UTF8);
            }
            catch { }
        }

        private static string AutoLocate()
        {
            var dir = Path.GetDirectoryName(Application.ExecutablePath);
            var same = Path.Combine(dir, Program.PclExeName);
            if (File.Exists(same)) return same;

            var probes = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.PclExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", Program.PclExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", Program.PclExeName),
            };
            foreach (var p in probes) if (File.Exists(p)) return p;
            return null;
        }

        private void BrowsePcl()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择 Plain Craft Launcher 2.exe";
                dlg.Filter = "PCL 主程序|Plain Craft Launcher 2.exe|可执行文件|*.exe";
                var cur = _pathBox.Text;
                if (!string.IsNullOrEmpty(cur) && File.Exists(cur))
                    dlg.InitialDirectory = Path.GetDirectoryName(cur);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dlg.FileName;
                    _remember.Checked = true;
                    SaveSettings();
                }
            }
        }

        private void StartPcl()
        {
            var path = _pathBox.Text.Trim();
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "找不到 PCL 主程序，请点「浏览…」选择 Plain Craft Launcher 2.exe。", Program.AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Path.GetFileName(path) != Program.PclExeName)
            {
                if (MessageBox.Show(this, "选中的文件不是 " + Program.PclExeName + "，仍要尝试吗？", Program.AppTitle,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            }
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)).Length > 0)
            {
                MessageBox.Show(this, "PCL 已经在运行了。请先完全退出 PCL（含托盘图标）再启动。", Program.AppTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SaveSettings();
            _start.Enabled = false;
            _status.Text = "正在启动 PCL 并应用修复…";
            Application.DoEvents();

            try
            {
                var info = Injector.Launch(path);
                _status.Text = "已启动：" + info;
                var t = new System.Windows.Forms.Timer { Interval = 1200 };
                t.Tick += (s, e) => { t.Stop(); Close(); };
                t.Start();
            }
            catch (Exception ex)
            {
                _start.Enabled = true;
                _status.Text = "";
                MessageBox.Show(this, ex.Message, Program.AppTitle + " - 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
