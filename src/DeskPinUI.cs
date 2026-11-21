// DeskPinUI - 桌面布局管理界面
// 只负责"列快照 + 调 DeskPin.exe"，所有读写都在子进程里做：
// 避免 UI 进程持有 shell COM 引用干扰写图标位置（这条踩过坑）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DeskPinUI
{
    public class ItemLite
    {
        public string name { get; set; }
        public int x { get; set; }
        public int y { get; set; }
    }

    public class SnapLite
    {
        public string name { get; set; }
        public string savedAt { get; set; }
        public int screenWidth { get; set; }
        public int screenHeight { get; set; }
        public int workWidth { get; set; }
        public int workHeight { get; set; }
        public List<ItemLite> items { get; set; }

        public SnapLite()
        {
            items = new List<ItemLite>();
        }
    }

    internal static class NativeUi
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                NativeUi.SetProcessDPIAware();
            }
            catch
            {
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal class InputDialog : Form
    {
        public string Value = "";
        private readonly TextBox tb;

        public InputDialog(string title, string prompt, string initial)
        {
            Text = title;
            Width = 430;
            Height = 180;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9F);

            Label lb = new Label();
            lb.Text = prompt;
            lb.SetBounds(16, 16, 390, 24);

            tb = new TextBox();
            tb.Text = initial;
            tb.SetBounds(16, 44, 380, 26);

            Button ok = new Button();
            ok.Text = "确定";
            ok.SetBounds(220, 88, 80, 28);
            ok.DialogResult = DialogResult.OK;

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.SetBounds(310, 88, 80, 28);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(lb);
            Controls.Add(tb);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            tb.SelectAll();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Value = tb.Text.Trim();
            base.OnFormClosing(e);
        }

        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            using (InputDialog d = new InputDialog(title, prompt, initial))
            {
                if (d.ShowDialog(owner) == DialogResult.OK)
                {
                    return d.Value;
                }
                return null;
            }
        }
    }

    internal class MainForm : Form
    {
        private readonly string baseDir;
        private readonly string exePath;
        private readonly string layoutDir;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private ListView list;
        private TextBox log;
        private ToolStripStatusLabel statusLabel;
        private Label infoLabel;
        private Button btnSave;
        private Button btnRestore;
        private Button btnVerify;
        private Button btnRename;
        private Button btnDelete;
        private Button btnRefresh;
        private Button btnOpen;
        private bool busy;

        public MainForm()
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            exePath = Path.Combine(baseDir, "DeskPin.exe");
            layoutDir = Path.Combine(baseDir, "layouts");

            Text = "桌面布局管理  ·  DeskPin";
            Width = 780;
            Height = 560;
            MinimumSize = new Size(640, 440);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = SystemIcons.Application;

            BuildUi();
            RefreshList();
            Log("提示：摆好桌面后点「保存当前布局」；乱了点「恢复选中」（或双击列表里的名字）。");
            Log("快照目录：" + layoutDir);
        }

        #region ---------- 界面 ----------

        private void BuildUi()
        {
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 52;
            top.Padding = new Padding(10, 10, 10, 6);
            top.WrapContents = false;
            top.AutoSize = false;

            btnSave = MakeButton("保存当前布局", 118);
            btnSave.Click += delegate { DoSave(); };

            btnRestore = MakeButton("恢复选中", 92);
            btnRestore.Click += delegate { DoRestoreSelected(); };

            btnVerify = MakeButton("核对", 62);
            btnVerify.Click += delegate { DoVerifySelected(); };

            btnRename = MakeButton("改名", 62);
            btnRename.Click += delegate { DoRenameSelected(); };

            btnDelete = MakeButton("删除", 62);
            btnDelete.Click += delegate { DoDeleteSelected(); };

            btnRefresh = MakeButton("刷新", 62);
            btnRefresh.Click += delegate { RefreshList(); };

            btnOpen = MakeButton("打开快照目录", 108);
            btnOpen.Click += delegate { OpenFolder(); };

            top.Controls.Add(btnSave);
            top.Controls.Add(btnRestore);
            top.Controls.Add(btnVerify);
            top.Controls.Add(btnRename);
            top.Controls.Add(btnDelete);
            top.Controls.Add(btnRefresh);
            top.Controls.Add(btnOpen);

            Panel infoPanel = new Panel();
            infoPanel.Dock = DockStyle.Top;
            infoPanel.Height = 28;

            infoLabel = new Label();
            infoLabel.Dock = DockStyle.Fill;
            infoLabel.TextAlign = ContentAlignment.MiddleLeft;
            infoLabel.Padding = new Padding(14, 0, 0, 0);
            infoLabel.ForeColor = Color.FromArgb(70, 70, 70);
            infoPanel.Controls.Add(infoLabel);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.Columns.Add("快照名称", 200);
            list.Columns.Add("图标数", 72);
            list.Columns.Add("保存时间", 160);
            list.Columns.Add("屏幕", 120);
            list.Columns[list.Columns.Count - 1].Width = -2;
            list.DoubleClick += delegate { DoRestoreSelected(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("恢复这套布局", null, delegate { DoRestoreSelected(); });
            menu.Items.Add("核对是否一致", null, delegate { DoVerifySelected(); });
            menu.Items.Add("改名", null, delegate { DoRenameSelected(); });
            menu.Items.Add("删除", null, delegate { DoDeleteSelected(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开快照目录", null, delegate { OpenFolder(); });
            list.ContextMenuStrip = menu;

            log = new TextBox();
            log.Dock = DockStyle.Bottom;
            log.Height = 150;
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.BackColor = Color.FromArgb(250, 250, 250);
            log.Font = new Font("Consolas", 9F);
            log.BorderStyle = BorderStyle.FixedSingle;

            StatusStrip strip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel("就绪");
            statusLabel.Spring = true;
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            strip.Items.Add(statusLabel);

            Controls.Add(list);
            Controls.Add(log);
            Controls.Add(infoPanel);
            Controls.Add(top);
            Controls.Add(strip);
        }

        private static Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 30;
            b.Margin = new Padding(0, 0, 8, 0);
            b.UseVisualStyleBackColor = true;
            return b;
        }

        #endregion

        #region ---------- 数据 ----------

        private void RefreshList()
        {
            string selected = SelectedName();

            list.BeginUpdate();
            list.Items.Clear();

            int count = 0;
            try
            {
                Directory.CreateDirectory(layoutDir);
                foreach (string file in Directory.GetFiles(layoutDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith("_")) continue;

                    ListViewItem row;
                    try
                    {
                        SnapLite s = Json.Deserialize<SnapLite>(File.ReadAllText(file, Encoding.UTF8));
                        if (s == null || s.items == null) continue;

                        row = new ListViewItem(name);
                        row.SubItems.Add(s.items.Count.ToString());
                        row.SubItems.Add(string.IsNullOrEmpty(s.savedAt) ? "-" : s.savedAt);
                        row.SubItems.Add(s.screenWidth > 0
                            ? (s.screenWidth + "x" + s.screenHeight)
                            : "-");
                    }
                    catch
                    {
                        row = new ListViewItem(name);
                        row.SubItems.Add("?");
                        row.SubItems.Add("读取失败");
                        row.SubItems.Add("-");
                    }

                    row.Tag = name;
                    list.Items.Add(row);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Log("[错误] 读快照目录失败：" + ex.Message);
            }

            list.EndUpdate();

            if (selected != null)
            {
                foreach (ListViewItem it in list.Items)
                {
                    if (string.Equals((string)it.Tag, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Selected = true;
                        it.Focused = true;
                        break;
                    }
                }
            }
            else if (list.Items.Count > 0)
            {
                list.Items[0].Selected = true;
            }

            infoLabel.Text = "共 " + count + " 个快照 · 目录 " + layoutDir;
        }

        private string SelectedName()
        {
            if (list.SelectedItems.Count == 0) return null;
            return (string)list.SelectedItems[0].Tag;
        }

        #endregion

        #region ---------- 动作 ----------

        private void DoSave()
        {
            if (busy) return;
            string initial = "布局-" + DateTime.Now.ToString("MMdd-HHmm");
            string name = InputDialog.Ask(this, "保存当前布局", "给这套布局起个名字（可用中文）：", initial);
            if (string.IsNullOrEmpty(name)) return;

            RunAsync("save \"" + name + "\"", "正在保存布局…", null);
        }

        private void DoRestoreSelected()
        {
            if (busy) return;
            string name = SelectedName();
            if (name == null)
            {
                Log("先在列表里选一个快照。");
                return;
            }

            RunAsync("restore \"" + name + "\"", "正在恢复「" + name + "」…（约 5~15 秒）", null);
        }

        private void DoVerifySelected()
        {
            if (busy) return;
            string name = SelectedName();
            if (name == null)
            {
                Log("先在列表里选一个快照。");
                return;
            }

            RunAsync("verify \"" + name + "\"", "正在核对…", null);
        }

        private void DoRenameSelected()
        {
            if (busy) return;
            string name = SelectedName();
            if (name == null)
            {
                Log("先在列表里选一个快照。");
                return;
            }

            string newName = InputDialog.Ask(this, "改名", "新名字：", name);
            if (string.IsNullOrEmpty(newName) || newName == name) return;

            RunAsync("rename \"" + name + "\" \"" + newName + "\"", "正在改名…", null);
        }

        private void DoDeleteSelected()
        {
            if (busy) return;
            string name = SelectedName();
            if (name == null)
            {
                Log("先在列表里选一个快照。");
                return;
            }

            if (MessageBox.Show(this, "删除快照「" + name + "」？\n（会留备份在 layouts\\_deleted\\）",
                    "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            {
                return;
            }

            RunAsync("delete \"" + name + "\"", "正在删除…", null);
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(layoutDir);
                Process.Start("explorer.exe", "\"" + layoutDir + "\"");
            }
            catch (Exception ex)
            {
                Log("[错误] 打开目录失败：" + ex.Message);
            }
        }

        #endregion

        #region ---------- 执行 ----------

        private void RunAsync(string arguments, string busyText, Action<string> done)
        {
            SetBusy(true, busyText);
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                int exitCode = 0;
                string output = "";
                try
                {
                    output = RunDeskPin(arguments, out exitCode);
                }
                catch (Exception ex)
                {
                    output = "[异常] " + ex.Message;
                    exitCode = -1;
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        SetBusy(false, exitCode == 0 ? "完成" : ("失败（退出码 " + exitCode + "）"));
                        Log("> DeskPin " + arguments);
                        if (!string.IsNullOrEmpty(output)) Log(output.TrimEnd());
                        RefreshList();
                        if (done != null) done(output);
                    });
                }
                catch
                {
                }
            });
        }

        private string RunDeskPin(string arguments, out int exitCode)
        {
            if (!File.Exists(exePath))
            {
                exitCode = -1;
                return "[错误] 找不到 " + exePath;
            }

            ProcessStartInfo psi = new ProcessStartInfo(exePath, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = baseDir;

            using (Process p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(180000);
                exitCode = p.ExitCode;
                return stdout + stderr;
            }
        }

        private void SetBusy(bool value, string text)
        {
            busy = value;
            btnSave.Enabled = !value;
            btnRestore.Enabled = !value;
            btnVerify.Enabled = !value;
            btnRename.Enabled = !value;
            btnDelete.Enabled = !value;
            btnRefresh.Enabled = !value;
            btnOpen.Enabled = !value;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
            statusLabel.Text = text;
        }

        private void Log(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (log.TextLength > 120000)
            {
                log.Text = log.Text.Substring(log.TextLength - 60000);
            }
            log.AppendText(line + Environment.NewLine);
        }

        #endregion
    }
}
