// DeskPin - 桌面图标布局快照 / 一键还原
// 目标：Windows 10 19045 / .NET Framework 4.0 (csc 4.0.30319, C# 5)
// 原理：Shell COM -> IShellWindows -> IShellBrowser -> IShellView -> IFolderView
//       GetItemPosition 读坐标，SelectAndPositionItems 写坐标（不重启 explorer）
// 特性：多快照 / 自定义命名 / 任意切换 / 桌面增删文件不报错 / 跨分辨率等比缩放
//
// 用法见 --help

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeskPin
{
    #region ---------- Win32 / COM 声明 ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ShellWindowsCoClass
    {
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
            [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
            [MarshalAs(UnmanagedType.I4)] int swClass,
            [MarshalAs(UnmanagedType.I4)] out int phwnd,
            [MarshalAs(UnmanagedType.I4)] int swfwOptions);
    }

    [ComImport]
    [Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IServiceProviderCom
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView(out IShellView ppshv);
        void OnViewWindowActive(IShellView ppshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IShellView psvPrevious, IntPtr pfs, IShellBrowser psb, ref RECT prcView, out IntPtr phwnd);
        void DestroyViewWindow();
        void GetCurrentInfo(out IntPtr pfs);
        void AddPanels(IntPtr pds);
        void RemovePanel(IntPtr pds);
        void GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
    }

    [ComImport]
    [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFolderView
    {
        void GetCurrentViewMode(out uint pViewMode);
        void SetCurrentViewMode(uint ViewMode);
        void GetFolder(ref Guid riid, out IntPtr ppv);
        void Item(int iItemIndex, out IntPtr ppidl);
        void ItemCount(uint uFlags, out int pcItems);
        void Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        void GetSelectionMarkedItem(out int piItem);
        void GetFocusedItem(out int piItem);
        void GetItemPosition(IntPtr pidl, out POINT ppt);
        void GetSpacing(out POINT ppt);
        void GetDefaultSpacing(out POINT ppt);
        [PreserveSig]
        int GetAutoArrange();
        void SelectItem(int iItem, uint dwFlags);
        [PreserveSig]
        int SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
    }

    internal static class Native
    {
        public const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
        public const int SWC_DESKTOP = 8;
        public const int SWFO_NEEDDISPATCH = 1;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);

        [DllImport("shell32.dll", PreserveSig = true)]
        public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("shell32.dll", PreserveSig = true)]
        public static extern void ILFree(IntPtr pidl);

        [DllImport("kernel32.dll", PreserveSig = true)]
        public static extern bool SetConsoleOutputCP(uint wCodePageID);

        public const uint WM_GETISHELLBROWSER = 0x0407;
        public const uint LVM_GETITEMCOUNT = 0x1004;
        public const uint LVM_SETITEMPOSITION = 0x100F;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }

    #endregion

    #region ---------- 快照模型 ----------

    public class SnapItem
    {
        public string name { get; set; }
        public int x { get; set; }
        public int y { get; set; }
    }

    public class RegVal
    {
        public string name { get; set; }
        public string kind { get; set; }
        public string data { get; set; }
    }

    public class Snapshot
    {
        public int version { get; set; }
        public string name { get; set; }
        public string savedAt { get; set; }
        public string savedBy { get; set; }
        public int screenWidth { get; set; }
        public int screenHeight { get; set; }
        public int workWidth { get; set; }
        public int workHeight { get; set; }
        public int vsLeft { get; set; }
        public int vsTop { get; set; }
        public int vsWidth { get; set; }
        public int vsHeight { get; set; }
        public string screens { get; set; }
        public uint viewMode { get; set; }
        public int iconSize { get; set; }
        public int spacingX { get; set; }
        public int spacingY { get; set; }
        public List<SnapItem> items { get; set; }
        public List<RegVal> reg { get; set; }

        public Snapshot()
        {
            version = 1;
            items = new List<SnapItem>();
            reg = new List<RegVal>();
            screens = "";
            name = "";
            savedAt = "";
            savedBy = "";
        }
    }

    public class ProbeRec
    {
        public int idx;
        public int x;
        public int y;
    }

    public class RestoreResult
    {
        public int placed;
        public int missing;
        public int extra;
        public int exact;
        public int totalInSnapshot;
        public bool rescaled;
        public int hresult;
        public int hresultFallback;
        public List<string> missingNames = new List<string>();
        public List<string> extraNames = new List<string>();
        public List<string> failedNames = new List<string>();
    }

    #endregion

    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // 最近一次拿到的 IShellView：还原后调 Refresh() 让视图缓存失效，校验读数才准
        private static IShellView g_lastView;

        private static string BaseDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private static string LayoutDir
        {
            get { return Path.Combine(BaseDir, "layouts"); }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Native.SetConsoleOutputCP(65001);
            }
            catch
            {
            }

            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
            }

            try
            {
                Directory.CreateDirectory(LayoutDir);
            }
            catch
            {
            }

            if (args == null || args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            string cmd = args[0].ToLowerInvariant();

            try
            {
                switch (cmd)
                {
                    case "save":
                        return CmdSave(args);
                    case "restore":
                    case "load":
                        return CmdRestore(args);
                    case "list":
                    case "ls":
                        return CmdList();
                    case "rename":
                        return CmdRename(args);
                    case "delete":
                    case "del":
                    case "rm":
                        return CmdDelete(args);
                    case "watch":
                        return CmdWatch(args);
                    case "status":
                        return CmdStatus();
                    case "where":
                        Console.WriteLine(LayoutDir);
                        return 0;
                    case "__probe":
                        return CmdProbe();
                    case "restoreapi":
                        return CmdRestoreApi(args);
                    case "lvmfix":
                        return CmdLvmFix(args);
                    case "__lvtest":
                    {
                        IntPtr lvv = GetDesktopListView();
                        Console.WriteLine("lv=" + lvv.ToString("X"));
                        Dictionary<string, ProbeRec> b = ProbeInChildProcess();
                        Console.WriteLine("before count=" + b.Count);
                        for (int k = 0; k < 5; k++) { LvmSetPos(lvv, k, 4623, 199 + k * 100); Thread.Sleep(120); }
                        Thread.Sleep(600);
                        Dictionary<string, ProbeRec> a2 = ProbeInChildProcess();
                        ProbeRec r0;
                        foreach (KeyValuePair<string, ProbeRec> kv in a2) { if (kv.Value.idx == 0) { r0 = kv.Value; Console.WriteLine("idx0 name=" + kv.Key + " pos=" + r0.x + "," + r0.y); } }
                        return 0;
                    }
                    case "verify":
                        return CmdVerify(args);
                    case "--help":
                    case "-h":
                    case "help":
                    case "/?":
                        PrintHelp();
                        return 0;
                    default:
                        Console.Error.WriteLine("未知命令：" + args[0]);
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[错误] " + ex.GetType().Name + ": " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine("        " + ex.InnerException.Message);
                }
                return 1;
            }
        }

        #region ---------- 命令实现 ----------

        private static int CmdSave(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("用法：DeskPin.exe save <名称>");
                return 2;
            }

            string name = Sanitize(args[1]);
            IFolderView fv = GetDesktopFolderView();

            Snapshot snap = Capture(fv, name);
            string path = LayoutPath(name);

            if (File.Exists(path))
            {
                string histDir = Path.Combine(LayoutDir, "_history");
                Directory.CreateDirectory(histDir);
                string bak = Path.Combine(histDir, name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                try
                {
                    File.Copy(path, bak, true);
                }
                catch
                {
                }
            }

            WriteSnapshot(path, snap);

            Console.WriteLine("已保存快照：" + name);
            Console.WriteLine("  文件      ：" + path);
            Console.WriteLine("  项数      ：" + snap.items.Count);
            Console.WriteLine("  屏幕      ：" + snap.screenWidth + "x" + snap.screenHeight + "（工作区 " + snap.workWidth + "x" + snap.workHeight + "）");
            Console.WriteLine("  视图/图标 ：mode=" + snap.viewMode + " icon=" + snap.iconSize + "px 间距=" + snap.spacingX + "x" + snap.spacingY);
            return 0;
        }

        private static int CmdRestore(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("用法：DeskPin.exe restore <名称> [--hard]");
                Console.Error.WriteLine("      --hard  改走注册表 + 重启 explorer（兜底，任务栏会闪 1~2 秒）");
                return 2;
            }

            bool hard = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--hard") hard = true;
            }

            string name = Sanitize(args[1]);
            string path = LayoutPath(name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("找不到快照：" + name);
                List<string> all = ListNames();
                if (all.Count == 0)
                {
                    Console.Error.WriteLine("（还没有任何快照，先执行 save）");
                }
                else
                {
                    Console.Error.WriteLine("可用快照：" + string.Join(", ", all.ToArray()));
                }
                return 3;
            }

            Snapshot snap = ReadSnapshot(path);

            if (hard && HasIconLayouts(snap.reg))
            {
                TryBackup();
                Console.WriteLine("正在还原：" + snap.name + "（保存于 " + snap.savedAt + "）");
                Console.WriteLine("  方式      ：写回注册表 IconLayouts + 重启 explorer");
                try
                {
                    KillExplorerAndApply(snap.reg);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[错误] 写注册表失败：" + ex.Message);
                    return 1;
                }
                ReportConsistency(snap);
                return 0;
            }

            Console.WriteLine("正在还原：" + snap.name + "（保存于 " + snap.savedAt + "）");

            int okApi = 0;
            try
            {
                IFolderView fv = GetDesktopFolderView();
                if (fv.GetAutoArrange() == 0)
                {
                    Console.Error.WriteLine("[注意] 桌面「自动排列图标」是开启的，位置会被系统立刻抹掉。");
                    Console.Error.WriteLine("       右键桌面 → 查看 → 取消「自动排列图标」后重试。");
                    return 4;
                }

                okApi = SetByApi(fv, snap);
                Console.WriteLine("  第一步    ：shell API 设置 " + okApi + " 项");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  [提示] API 路径不可用（" + ex.Message + "），改用注册表硬还原");
                if (!HasIconLayouts(snap.reg))
                {
                    return 1;
                }
                KillExplorerAndApply(snap.reg);
                ReportConsistency(snap);
                return 0;
            }

            Thread.Sleep(250);

            // 跨显示器（副屏 200% 缩放）的项 API 会换算失败，交给独立进程用 ListView 物理像素补齐
            try
            {
                string outp = RunChild("lvmfix \"" + name + "\"");
                if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine("  第二步    ：" + outp.Trim());
            }
            catch
            {
            }

            Thread.Sleep(300);
            ReportConsistency(snap);
            PrintRestoreNotes(snap);
            return 0;
        }

        private static void PrintRestoreNotes(Snapshot snap)
        {
            if (snap.workWidth > 0 && snap.workHeight > 0 &&
                (snap.workWidth != SystemInformation.WorkingArea.Width ||
                 snap.workHeight != SystemInformation.WorkingArea.Height))
            {
                Console.WriteLine("  说明      ：保存时工作区 " + snap.workWidth + "x" + snap.workHeight +
                                  "，当前 " + SystemInformation.WorkingArea.Width + "x" + SystemInformation.WorkingArea.Height);
            }
        }

        private static void TryBackup()
        {
            try
            {
                IFolderView fv0 = GetDesktopFolderView();
                Snapshot before = Capture(fv0, "_before-restore");
                WriteSnapshot(Path.Combine(LayoutDir, "_before-restore.json"), before);
            }
            catch
            {
            }
        }

        private static void ReportConsistency(Snapshot snap)
        {
            try
            {
                IFolderView fv = GetDesktopFolderView();
                Dictionary<string, ProbeRec> now = ProbeInChildProcess();
                int ok = 0;
                int absent = 0;
                int bad = 0;
                foreach (SnapItem it in snap.items)
                {
                    ProbeRec cur;
                    if (!now.TryGetValue(it.name, out cur))
                    {
                        absent++;
                        continue;
                    }
                    if (Math.Abs(cur.x - it.x) <= 2 && Math.Abs(cur.y - it.y) <= 2) ok++;
                    else bad++;
                }

                Console.WriteLine("  核对结果  ：一致 " + ok + " / " + snap.items.Count +
                                  "，未到位 " + bad + "，已不存在 " + absent);
                if (bad > 0)
                {
                    Console.WriteLine("  提示      ：再执行一次 restore " + snap.name + " 通常可把剩余项补齐");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  核对跳过  ：" + ex.Message);
            }
        }

        private static string RunChild(string arguments)
        {
            string exe = Assembly.GetEntryAssembly().Location;
            if (string.IsNullOrEmpty(exe)) return "";
            ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(120000);
                return outp + err;
            }
        }

        private static int SetByApi(IFolderView fv, Snapshot snap)
        {
            int ok = 0;
            foreach (SnapItem it in snap.items)
            {
                IntPtr full = IntPtr.Zero;
                uint attrs = 0;
                if (Native.SHParseDisplayName(it.name, IntPtr.Zero, out full, 0, out attrs) != 0 || full == IntPtr.Zero) continue;

                IntPtr parentUnk = IntPtr.Zero;
                IntPtr child = IntPtr.Zero;
                try
                {
                    Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
                    Native.SHBindToParent(full, ref iidUnknown, out parentUnk, out child);
                }
                catch
                {
                    child = IntPtr.Zero;
                }

                if (child == IntPtr.Zero)
                {
                    Native.ILFree(full);
                    if (parentUnk != IntPtr.Zero) { try { Marshal.Release(parentUnk); } catch { } }
                    continue;
                }

                IntPtr arrPidl = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr arrPt = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(POINT)));
                try
                {
                    Marshal.WriteIntPtr(arrPidl, 0, child);
                    POINT pt;
                    pt.X = it.x;
                    pt.Y = it.y;
                    Marshal.StructureToPtr(pt, arrPt, false);
                    if (fv.SelectAndPositionItems(1, arrPidl, arrPt, 0) == 0) ok++;
                }
                finally
                {
                    Marshal.FreeHGlobal(arrPidl);
                    Marshal.FreeHGlobal(arrPt);
                    Native.ILFree(full);
                    if (parentUnk != IntPtr.Zero) { try { Marshal.Release(parentUnk); } catch { } }
                }

                Thread.Sleep(20);
            }
            return ok;
        }

        private static int CmdList()
        {
            List<string> names = ListNames();
            if (names.Count == 0)
            {
                Console.WriteLine("还没有任何快照。先摆好桌面，再执行：DeskPin.exe save <名称>");
                return 0;
            }

            Console.WriteLine("快照目录：" + LayoutDir);
            Console.WriteLine();
            Console.WriteLine(Pad("名称", 20) + Pad("项数", 6) + Pad("屏幕", 13) + "保存时间");
            Console.WriteLine(new string('-', 66));
            foreach (string n in names)
            {
                Snapshot s;
                try
                {
                    s = ReadSnapshot(LayoutPath(n));
                }
                catch
                {
                    Console.WriteLine(Pad(n, 20) + Pad("?", 6) + Pad("-", 13) + "读取失败");
                    continue;
                }

                string scr = s.screenWidth + "x" + s.screenHeight;
                Console.WriteLine(Pad(n, 20) + Pad(s.items.Count.ToString(CultureInfo.InvariantCulture), 6) + Pad(scr, 13) + s.savedAt);
            }
            return 0;
        }

        private static int CmdRename(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("用法：DeskPin.exe rename <旧名> <新名>");
                return 2;
            }

            string oldName = Sanitize(args[1]);
            string newName = Sanitize(args[2]);
            string from = LayoutPath(oldName);
            string to = LayoutPath(newName);

            if (!File.Exists(from))
            {
                Console.Error.WriteLine("找不到快照：" + oldName);
                return 3;
            }
            if (File.Exists(to))
            {
                Console.Error.WriteLine("目标名称已存在：" + newName);
                return 3;
            }

            Snapshot s = ReadSnapshot(from);
            s.name = newName;
            WriteSnapshot(to, s);
            File.Delete(from);
            Console.WriteLine("已重命名：" + oldName + " → " + newName);
            return 0;
        }

        private static int CmdDelete(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法：DeskPin.exe delete <名称>");
                return 2;
            }

            string name = Sanitize(args[1]);
            string path = LayoutPath(name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("找不到快照：" + name);
                return 3;
            }

            string trash = Path.Combine(LayoutDir, "_deleted");
            Directory.CreateDirectory(trash);
            string dest = Path.Combine(trash, name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Move(path, dest);
            Console.WriteLine("已删除快照：" + name);
            Console.WriteLine("  备份留在：" + dest);
            return 0;
        }

        private static int CmdVerify(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                Console.Error.WriteLine("用法：DeskPin.exe verify <名称>");
                return 2;
            }

            string name = Sanitize(args[1]);
            string path = LayoutPath(name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("找不到快照：" + name);
                return 3;
            }

            Snapshot snap = ReadSnapshot(path);
            IFolderView fv = GetDesktopFolderView();

            System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;
            double sx = 1.0;
            double sy = 1.0;
            bool rescaled = false;
            if (snap.vsWidth > 0 && snap.vsHeight > 0 &&
                (snap.vsWidth != vs.Width || snap.vsHeight != vs.Height))
            {
                sx = (double)vs.Width / (double)snap.vsWidth;
                sy = (double)vs.Height / (double)snap.vsHeight;
                rescaled = true;
            }

            Dictionary<string, POINT> now = SnapshotPositions(fv);

            int ok = 0;
            int moved = 0;
            int absent = 0;
            int maxDelta = 0;
            List<string> movedNames = new List<string>();

            foreach (SnapItem it in snap.items)
            {
                int tx;
                int ty;
                if (rescaled)
                {
                    tx = vs.Left + (int)Math.Round((it.x - snap.vsLeft) * sx);
                    ty = vs.Top + (int)Math.Round((it.y - snap.vsTop) * sy);
                }
                else
                {
                    tx = it.x;
                    ty = it.y;
                }

                POINT cur;
                if (!now.TryGetValue(it.name, out cur))
                {
                    absent++;
                    continue;
                }

                int d = Math.Max(Math.Abs(cur.X - tx), Math.Abs(cur.Y - ty));
                if (d > maxDelta) maxDelta = d;
                if (d <= 2)
                {
                    ok++;
                }
                else
                {
                    moved++;
                    if (movedNames.Count < 32)
                    {
                        movedNames.Add(ShortName(it.name) + " 目标(" + tx + "," + ty + ") 实际(" + cur.X + "," + cur.Y + ")");
                    }
                }
            }

            Console.WriteLine("核对快照：" + snap.name);
            Console.WriteLine("  项数      ：" + snap.items.Count);
            Console.WriteLine("  位置正确  ：" + ok);
            Console.WriteLine("  位置不符  ：" + moved + (moved > 0 ? "（最大偏差 " + maxDelta + "px）" : ""));
            Console.WriteLine("  已不存在  ：" + absent);
            if (movedNames.Count > 0)
            {
                Console.WriteLine("  明细      ：");
                foreach (string m in movedNames) Console.WriteLine("    · " + m);
            }
            if (ok == snap.items.Count - absent && moved == 0)
            {
                Console.WriteLine("  结论      ：与快照一致");
            }
            else if (moved > 0)
            {
                Console.WriteLine("  结论      ：有 " + moved + " 项未到位，可再执行一次 restore " + snap.name);
            }
            return 0;
        }

        // 只给父进程用：把当前每项坐标以 "name<TAB>x<TAB>y" 打出来。
        // 必须用独立进程读，本进程内的 IFolderView 在 SelectAndPositionItems 之后有视图缓存，
        // GetItemPosition 会返回旧值。
        private static int CmdProbe()
        {
            IFolderView fv = GetDesktopFolderView();
            int count = 0;
            fv.ItemCount(0, out count);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    fv.Item(i, out pidl);
                    if (pidl == IntPtr.Zero) continue;

                    string parsing;
                    if (!TryGetParsingName(pidl, out parsing)) continue;
                    if (string.IsNullOrEmpty(parsing)) continue;

                    POINT pt;
                    fv.GetItemPosition(pidl, out pt);
                    sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(parsing).Append('\t')
                      .Append(pt.X.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(pt.Y.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                    {
                        try { Native.ILFree(pidl); }
                        catch { }
                    }
                }
            }

            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
            return 0;
        }

        private static Dictionary<string, ProbeRec> ProbeInChildProcess()
        {
            Dictionary<string, ProbeRec> map = new Dictionary<string, ProbeRec>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string exe = Assembly.GetEntryAssembly().Location;
                if (string.IsNullOrEmpty(exe)) return map;

                ProcessStartInfo psi = new ProcessStartInfo(exe, "__probe");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);

                    foreach (string raw in outp.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        if (line.Length == 0) continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length != 4) continue;

                        int idx;
                        int x;
                        int y;
                        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) continue;
                        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) continue;
                        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) continue;

                        ProbeRec rec = new ProbeRec();
                        rec.idx = idx;
                        rec.x = x;
                        rec.y = y;
                        map[parts[1]] = rec;
                    }
                }
            }
            catch
            {
            }
            return map;
        }

        private static int CmdLvmFix(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法：DeskPin.exe lvmfix <名称>");
                return 2;
            }

            string name = Sanitize(args[1]);
            string path = LayoutPath(name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("找不到快照：" + name);
                return 3;
            }

            Snapshot snap = ReadSnapshot(path);
            IntPtr lv = GetDesktopListView();
            if (lv == IntPtr.Zero)
            {
                Console.Error.WriteLine("拿不到桌面列表控件");
                return 4;
            }

            Dictionary<string, ProbeRec> live = ProbeInChildProcess();
            int wrote = 0;
            int skipped = 0;

            foreach (SnapItem it in snap.items)
            {
                ProbeRec rec;
                if (!live.TryGetValue(it.name, out rec)) { skipped++; continue; }
                if (Math.Abs(rec.x - it.x) <= 2 && Math.Abs(rec.y - it.y) <= 2) continue;
                LvmSetPos(lv, rec.idx, it.x, it.y);
                wrote++;
                Thread.Sleep(120);
            }

            Console.WriteLine("lvmfix：写入 " + wrote + " 项，跳过 " + skipped + " 项");
            return 0;
        }

        private static int CmdRestoreApi(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("用法：DeskPin.exe restoreapi <名称>");
                return 2;
            }

            string name = Sanitize(args[1]);
            string path = LayoutPath(name);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine("找不到快照：" + name);
                return 3;
            }

            Snapshot snap = ReadSnapshot(path);
            IFolderView fv = GetDesktopFolderView();

            int ok = 0;
            int fail = 0;

            foreach (SnapItem it in snap.items)
            {
                IntPtr full = IntPtr.Zero;
                uint attrs = 0;
                if (Native.SHParseDisplayName(it.name, IntPtr.Zero, out full, 0, out attrs) != 0 || full == IntPtr.Zero)
                {
                    fail++;
                    continue;
                }

                IntPtr parentUnk = IntPtr.Zero;
                IntPtr child = IntPtr.Zero;
                try
                {
                    Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
                    Native.SHBindToParent(full, ref iidUnknown, out parentUnk, out child);
                }
                catch
                {
                    child = IntPtr.Zero;
                }

                if (child == IntPtr.Zero)
                {
                    fail++;
                    Native.ILFree(full);
                    if (parentUnk != IntPtr.Zero) { try { Marshal.Release(parentUnk); } catch { } }
                    continue;
                }

                IntPtr arrPidl = Marshal.AllocHGlobal(IntPtr.Size);
                IntPtr arrPt = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(POINT)));
                try
                {
                    Marshal.WriteIntPtr(arrPidl, 0, child);
                    POINT pt;
                    pt.X = it.x;
                    pt.Y = it.y;
                    Marshal.StructureToPtr(pt, arrPt, false);
                    int hr = fv.SelectAndPositionItems(1, arrPidl, arrPt, 0);
                    if (hr == 0) ok++; else fail++;
                }
                finally
                {
                    Marshal.FreeHGlobal(arrPidl);
                    Marshal.FreeHGlobal(arrPt);
                    Native.ILFree(full);
                    if (parentUnk != IntPtr.Zero) { try { Marshal.Release(parentUnk); } catch { } }
                }

                Thread.Sleep(25);
            }

            Console.WriteLine("API 逐个设置：调用成功 " + ok + "，失败 " + fail);
            return 0;
        }

        private static int CmdStatus()
        {
            IFolderView fv = GetDesktopFolderView();
            Snapshot s = Capture(fv, "_status");

            Console.WriteLine("屏幕配置");
            foreach (Screen sc in Screen.AllScreens)
            {
                Console.WriteLine("  " + sc.DeviceName + "  " + sc.Bounds.Width + "x" + sc.Bounds.Height +
                                  "  主屏=" + (sc.Primary ? "是" : "否"));
            }
            Console.WriteLine("  主屏工作区：" + SystemInformation.WorkingArea.Width + "x" + SystemInformation.WorkingArea.Height);
            Console.WriteLine();
            Console.WriteLine("桌面视图");
            Console.WriteLine("  图标数：" + s.items.Count);
            Console.WriteLine("  viewMode=" + s.viewMode + "  iconSize=" + s.iconSize + "px  间距=" + s.spacingX + "x" + s.spacingY);
            Console.WriteLine("  自动排列图标：" + (fv.GetAutoArrange() == 0 ? "开启（会导致还原失效）" : "关闭（正确）"));
            Console.WriteLine();
            Console.WriteLine("快照目录：" + LayoutDir);
            List<string> names = ListNames();
            Console.WriteLine("已有快照：" + (names.Count == 0 ? "（无）" : string.Join(", ", names.ToArray())));
            return 0;
        }

        private static int CmdWatch(string[] args)
        {
            string name = "main";
            int interval = 800;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--interval" && i + 1 < args.Length)
                {
                    int v;
                    if (int.TryParse(args[i + 1], out v) && v >= 300) interval = v;
                    i++;
                }
                else if (!args[i].StartsWith("--"))
                {
                    name = Sanitize(args[i]);
                }
            }

            if (!File.Exists(LayoutPath(name)))
            {
                Console.Error.WriteLine("找不到快照：" + name + "（先执行 DeskPin.exe save " + name + "）");
                return 3;
            }

            Console.WriteLine("DeskPin watch 已启动");
            Console.WriteLine("  监控快照：" + name);
            Console.WriteLine("  轮询间隔：" + interval + "ms");
            Console.WriteLine("  触发条件：屏幕数量 / 分辨率 / 工作区变化，稳定 2 秒后自动还原");
            Console.WriteLine("  停止方式：Ctrl+C 或关闭本窗口");

            string last = Signature();
            DateTime lastChange = DateTime.MinValue;
            bool pending = false;

            while (true)
            {
                Thread.Sleep(interval);
                string cur = Signature();
                if (cur != last)
                {
                    last = cur;
                    pending = true;
                    lastChange = DateTime.Now;
                    Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 显示配置变化：" + cur);
                    continue;
                }

                if (pending && (DateTime.Now - lastChange).TotalSeconds >= 2.0)
                {
                    pending = false;
                    try
                    {
                        Snapshot snap = ReadSnapshot(LayoutPath(name));
                        IFolderView fv = GetDesktopFolderView();
                        if (fv.GetAutoArrange() == 0)
                        {
                            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 自动排列开启，跳过");
                            continue;
                        }
                        RestoreResult r = Restore(fv, snap);
                        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 已自动还原 " + r.placed + "/" + r.totalInSnapshot +
                                          "（缺失 " + r.missing + "，新增 " + r.extra + "）");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] 还原失败：" + ex.Message);
                    }
                }
            }
        }

        #endregion

        #region ---------- 布局读写 ----------

        private static IFolderView GetDesktopFolderView()
        {
            // 路线一（首选）：Progman / WorkerW 下的 SHELLDLL_DefView，
            // 用未文档化消息 WM_GETISHELLBROWSER(0x0407) 直接取 IShellBrowser*。
            // 不依赖 IDispatch 的名字解析，最稳。
            IntPtr defView = FindDesktopDefView();
            if (defView != IntPtr.Zero)
            {
                IntPtr browserPtr = Native.SendMessage(defView, Native.WM_GETISHELLBROWSER, IntPtr.Zero, IntPtr.Zero);
                if (browserPtr != IntPtr.Zero)
                {
                    object obj = Marshal.GetObjectForIUnknown(browserPtr);
                    IShellBrowser viaMsg = obj as IShellBrowser;
                    if (viaMsg != null)
                    {
                        IFolderView fv = GetFolderViewFromBrowser(viaMsg);
                        if (fv != null) return fv;
                    }
                }
            }

            // 路线二（兜底）：IShellWindows.FindWindowSW
            object swObj = new ShellWindowsCoClass();
            IShellWindows sw = (IShellWindows)swObj;

            object loc = null;
            object root = null;
            int hwnd;
            object browserDisp = sw.FindWindowSW(ref loc, ref root, Native.SWC_DESKTOP, out hwnd, Native.SWFO_NEEDDISPATCH);

            if (browserDisp == null)
            {
                throw new InvalidOperationException("找不到桌面视图（explorer 是否在运行？）");
            }

            IShellBrowser browser = null;

            IServiceProviderCom sp = browserDisp as IServiceProviderCom;
            if (sp != null)
            {
                Guid sidSTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                Guid iidBrowser = typeof(IShellBrowser).GUID;
                try
                {
                    object raw = sp.QueryService(ref sidSTopLevelBrowser, ref iidBrowser);
                    browser = raw as IShellBrowser;
                }
                catch
                {
                    browser = null;
                }
            }

            if (browser == null)
            {
                browser = browserDisp as IShellBrowser;
            }

            if (browser == null)
            {
                throw new InvalidOperationException("无法取得 IShellBrowser");
            }

            IFolderView fv2 = GetFolderViewFromBrowser(browser);
            if (fv2 == null)
            {
                throw new InvalidOperationException("无法取得 IFolderView（桌面视图未就绪）");
            }
            return fv2;
        }

        private static IFolderView GetFolderViewFromBrowser(IShellBrowser browser)
        {
            IShellView view;
            try
            {
                browser.QueryActiveShellView(out view);
            }
            catch
            {
                return null;
            }
            if (view == null) return null;
            g_lastView = view;
            return view as IFolderView;
        }

        private static IntPtr GetDesktopListView()
        {
            IntPtr defView = FindDesktopDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;
            return Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        // 跨进程只能用"值"参数的消息（LVM_SETITEMPOSITION 正好是值，不是指针）
        private static void LvmSetPos(IntPtr lv, int index, int x, int y)
        {
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x > 32000) x = 32000;
            if (y > 32000) y = 32000;
            int packed = ((y & 0xFFFF) << 16) | (x & 0xFFFF);
            Native.SendMessage(lv, Native.LVM_SETITEMPOSITION, (IntPtr)index, (IntPtr)packed);
        }

        private static IntPtr FindDesktopDefView()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr dv = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) return dv;
            }

            IntPtr found = IntPtr.Zero;
            Native.EnumWindowsProc proc = delegate(IntPtr hWnd, IntPtr lParam)
            {
                IntPtr dv = Native.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero)
                {
                    found = dv;
                    return false;
                }
                return true;
            };

            try
            {
                Native.EnumWindows(proc, IntPtr.Zero);
            }
            catch
            {
            }

            GC.KeepAlive(proc);
            return found;
        }

        private static Snapshot Capture(IFolderView fv, string name)
        {
            Snapshot s = new Snapshot();
            s.name = name;
            s.savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            s.savedBy = Environment.UserName;

            System.Drawing.Rectangle wa = SystemInformation.WorkingArea;
            s.workWidth = wa.Width;
            s.workHeight = wa.Height;

            System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;
            s.vsLeft = vs.Left;
            s.vsTop = vs.Top;
            s.vsWidth = vs.Width;
            s.vsHeight = vs.Height;

            Screen primary = Screen.PrimaryScreen;
            if (primary != null)
            {
                s.screenWidth = primary.Bounds.Width;
                s.screenHeight = primary.Bounds.Height;
            }

            StringBuilder sb = new StringBuilder();
            foreach (Screen sc in Screen.AllScreens)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(sc.DeviceName).Append(":").Append(sc.Bounds.Width).Append("x").Append(sc.Bounds.Height);
                if (sc.Primary) sb.Append("*");
            }
            s.screens = sb.ToString();

            uint mode = 0;
            try
            {
                fv.GetCurrentViewMode(out mode);
            }
            catch
            {
            }
            s.viewMode = mode;

            POINT spacing = new POINT();
            try
            {
                fv.GetSpacing(out spacing);
            }
            catch
            {
            }
            s.spacingX = spacing.X;
            s.spacingY = spacing.Y;

            s.iconSize = ReadIconSize();
            s.reg = ReadBagValues();

            int count = 0;
            fv.ItemCount(0, out count);

            for (int i = 0; i < count; i++)
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    fv.Item(i, out pidl);
                    if (pidl == IntPtr.Zero) continue;

                    string parsing;
                    if (!TryGetParsingName(pidl, out parsing)) continue;
                    if (string.IsNullOrEmpty(parsing)) continue;

                    POINT pt;
                    fv.GetItemPosition(pidl, out pt);

                    SnapItem it = new SnapItem();
                    it.name = parsing;
                    it.x = pt.X;
                    it.y = pt.Y;
                    s.items.Add(it);
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                    {
                        try { Native.ILFree(pidl); }
                        catch { }
                    }
                }
            }

            return s;
        }

        private static RestoreResult Restore(IFolderView fv, Snapshot snap)
        {
            RestoreResult res = new RestoreResult();
            if (snap == null || snap.items == null)
            {
                return res;
            }
            res.totalInSnapshot = snap.items.Count;

            System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;
            System.Drawing.Rectangle wa = SystemInformation.WorkingArea;

            double sx = 1.0;
            double sy = 1.0;

            if (snap.vsWidth > 0 && snap.vsHeight > 0 &&
                (snap.vsWidth != vs.Width || snap.vsHeight != vs.Height))
            {
                sx = (double)vs.Width / (double)snap.vsWidth;
                sy = (double)vs.Height / (double)snap.vsHeight;
                res.rescaled = true;
            }

            // SelectAndPositionItems 需要的是"相对桌面 folder 的单层 child PIDL"，
            // SHParseDisplayName 给的是完全限定 PIDL，直接传进去 shell 会静默忽略。
            // 用 SHBindToParent 取最后一段，并保持 fullPIDL 存活到调用结束。
            List<IntPtr> fullPidls = new List<IntPtr>();
            List<IntPtr> parentUnks = new List<IntPtr>();
            List<IntPtr> pidls = new List<IntPtr>();
            List<POINT> points = new List<POINT>();
            List<string> names = new List<string>();
            List<int> targetX = new List<int>();
            List<int> targetY = new List<int>();

            foreach (SnapItem it in snap.items)
            {
                IntPtr pidl = IntPtr.Zero;
                uint attrs = 0;
                int hr;
                try
                {
                    hr = Native.SHParseDisplayName(it.name, IntPtr.Zero, out pidl, 0, out attrs);
                }
                catch
                {
                    hr = -1;
                }

                if (hr != 0 || pidl == IntPtr.Zero)
                {
                    res.missing++;
                    if (res.missingNames.Count < 32) res.missingNames.Add(ShortName(it.name));
                    continue;
                }

                IntPtr parentUnk = IntPtr.Zero;
                IntPtr childPidl = IntPtr.Zero;
                try
                {
                    Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
                    int hr2 = Native.SHBindToParent(pidl, ref iidUnknown, out parentUnk, out childPidl);
                    if (hr2 != 0 || childPidl == IntPtr.Zero)
                    {
                        childPidl = IntPtr.Zero;
                    }
                }
                catch
                {
                    childPidl = IntPtr.Zero;
                }

                if (childPidl == IntPtr.Zero)
                {
                    res.missing++;
                    if (res.missingNames.Count < 32) res.missingNames.Add(ShortName(it.name));
                    if (parentUnk != IntPtr.Zero) { try { Marshal.Release(parentUnk); } catch { } }
                    try { Native.ILFree(pidl); } catch { }
                    continue;
                }

                fullPidls.Add(pidl);
                parentUnks.Add(parentUnk);

                int x;
                int y;
                if (res.rescaled)
                {
                    x = vs.Left + (int)Math.Round((it.x - snap.vsLeft) * sx);
                    y = vs.Top + (int)Math.Round((it.y - snap.vsTop) * sy);
                }
                else
                {
                    x = it.x;
                    y = it.y;
                }

                if (x < vs.Left) x = vs.Left;
                if (y < vs.Top) y = vs.Top;
                if (x > vs.Right - 8) x = vs.Right - 8;
                if (y > vs.Bottom - 8) y = vs.Bottom - 8;

                pidls.Add(childPidl);
                POINT p = new POINT();
                p.X = x;
                p.Y = y;
                points.Add(p);
                names.Add(it.name);
                targetX.Add(x);
                targetY.Add(y);
            }

            List<int> order = new List<int>();
            for (int i = 0; i < pidls.Count; i++) order.Add(i);
            order.Sort(delegate(int a, int b)
            {
                if (points[a].Y != points[b].Y) return points[a].Y.CompareTo(points[b].Y);
                return points[a].X.CompareTo(points[b].X);
            });

            List<IntPtr> sPidl = new List<IntPtr>();
            List<POINT> sPt = new List<POINT>();
            foreach (int idx in order)
            {
                sPidl.Add(pidls[idx]);
                sPt.Add(points[idx]);
            }

            // 当前视图里 "名字 -> 列表索引"，避免新增/移动文件后索引漂移导致错位
            Dictionary<string, int> liveIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int liveCount = 0;
            try
            {
                fv.ItemCount(0, out liveCount);
                for (int i = 0; i < liveCount; i++)
                {
                    IntPtr lp = IntPtr.Zero;
                    try
                    {
                        fv.Item(i, out lp);
                        if (lp == IntPtr.Zero) continue;
                        string nm;
                        if (TryGetParsingName(lp, out nm) && !string.IsNullOrEmpty(nm))
                        {
                            if (!liveIndex.ContainsKey(nm)) liveIndex[nm] = i;
                        }
                    }
                    finally
                    {
                        if (lp != IntPtr.Zero)
                        {
                            try { Native.ILFree(lp); }
                            catch { }
                        }
                    }
                }
            }
            catch
            {
            }

            IntPtr listView = GetDesktopListView();
            int lvDone = 0;

            // 写位置统一走 RestoreSoft（通过子进程读坐标 + 纯 Win32 写，避免持有 COM 引用干扰）

            res.placed = pidls.Count;

            foreach (IntPtr p in fullPidls)
            {
                try { Native.ILFree(p); }
                catch { }
            }
            foreach (IntPtr u in parentUnks)
            {
                if (u != IntPtr.Zero)
                {
                    try { Marshal.Release(u); }
                    catch { }
                }
            }

            try
            {
                HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SnapItem it in snap.items) known.Add(it.name);

                int count = 0;
                fv.ItemCount(0, out count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr pidl = IntPtr.Zero;
                    try
                    {
                        fv.Item(i, out pidl);
                        if (pidl == IntPtr.Zero) continue;

                        string parsing;
                        if (!TryGetParsingName(pidl, out parsing)) continue;
                        if (string.IsNullOrEmpty(parsing)) continue;
                        if (!known.Contains(parsing))
                        {
                            res.extra++;
                            if (res.extraNames.Count < 32) res.extraNames.Add(ShortName(parsing));
                        }
                    }
                    finally
                    {
                        if (pidl != IntPtr.Zero)
                        {
                            try { Native.ILFree(pidl); }
                            catch { }
                        }
                    }
                }
            }
            catch
            {
            }

            return res;
        }

        #endregion

        #region ---------- 工具 ----------

        private const string BagPath = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";

        private static List<RegVal> ReadBagValues()
        {
            List<RegVal> list = new List<RegVal>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(BagPath))
                {
                    if (k == null) return list;
                    foreach (string n in k.GetValueNames())
                    {
                        try
                        {
                            RegistryValueKind kind = k.GetValueKind(n);
                            object v = k.GetValue(n);
                            RegVal rv = new RegVal();
                            rv.name = n;

                            if (kind == RegistryValueKind.Binary && v is byte[])
                            {
                                rv.kind = "Binary";
                                rv.data = Convert.ToBase64String((byte[])v);
                            }
                            else if (kind == RegistryValueKind.DWord && v != null)
                            {
                                rv.kind = "DWord";
                                rv.data = Convert.ToInt32(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                            }
                            else if (kind == RegistryValueKind.QWord && v != null)
                            {
                                rv.kind = "QWord";
                                rv.data = Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                            }
                            else if (kind == RegistryValueKind.String && v is string)
                            {
                                rv.kind = "String";
                                rv.data = (string)v;
                            }
                            else
                            {
                                continue;
                            }

                            list.Add(rv);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            return list;
        }

        private static void WriteBagValues(List<RegVal> vals)
        {
            if (vals == null || vals.Count == 0) return;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(BagPath, true))
            {
                if (k == null)
                {
                    throw new InvalidOperationException("打不开注册表键：" + BagPath);
                }
                foreach (RegVal rv in vals)
                {
                    if (rv == null || string.IsNullOrEmpty(rv.name)) continue;
                    if (rv.kind == "Binary")
                    {
                        k.SetValue(rv.name, Convert.FromBase64String(rv.data), RegistryValueKind.Binary);
                    }
                    else if (rv.kind == "DWord")
                    {
                        k.SetValue(rv.name, int.Parse(rv.data, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                    }
                    else if (rv.kind == "QWord")
                    {
                        k.SetValue(rv.name, long.Parse(rv.data, CultureInfo.InvariantCulture), RegistryValueKind.QWord);
                    }
                    else if (rv.kind == "String")
                    {
                        k.SetValue(rv.name, rv.data, RegistryValueKind.String);
                    }
                }
            }
        }

        private static bool HasIconLayouts(List<RegVal> vals)
        {
            if (vals == null) return false;
            foreach (RegVal rv in vals)
            {
                if (string.Equals(rv.name, "IconLayouts", StringComparison.OrdinalIgnoreCase) && rv.kind == "Binary" && rv.data.Length > 64)
                {
                    return true;
                }
            }
            return false;
        }

        private static void KillExplorerAndApply(List<RegVal> vals)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/f /im explorer.exe");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                }
            }
            catch
            {
            }

            for (int i = 0; i < 40; i++)
            {
                if (Process.GetProcessesByName("explorer").Length == 0) break;
                Thread.Sleep(100);
            }

            Thread.Sleep(400);
            WriteBagValues(vals);
            Thread.Sleep(200);

            try
            {
                Process.Start("explorer.exe");
            }
            catch
            {
                try
                {
                    Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                }
                catch
                {
                }
            }

            for (int i = 0; i < 60; i++)
            {
                if (Process.GetProcessesByName("explorer").Length > 0) break;
                Thread.Sleep(100);
            }
            Thread.Sleep(2200);
        }

        private static List<POINT> FindFreeCells(
            Dictionary<string, string> occ, int need, int stepX, int stepY,
            int ox, int oy, int maxX, int maxY)
        {
            List<POINT> list = new List<POINT>();
            if (need <= 0 || stepX <= 0 || stepY <= 0) return list;

            for (int y = oy; y <= maxY && list.Count < need; y += stepY)
            {
                for (int x = ox; x <= maxX && list.Count < need; x += stepX)
                {
                    string key = x + "," + y;
                    if (occ.ContainsKey(key)) continue;
                    POINT p;
                    p.X = x;
                    p.Y = y;
                    list.Add(p);
                    occ[key] = "__free__";
                }
            }
            return list;
        }

        private static POINT FindFreeCell(Dictionary<string, string> cellOwner, int stepX, int stepY)
        {
            POINT none;
            none.X = -1;
            none.Y = -1;

            System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;

            int left = (int)(vs.Width * 0.55);
            int top = 8;
            int right = vs.Width - 40;
            int bottom = (int)(vs.Height * 0.92);

            for (int y = top; y < bottom; y += stepY)
            {
                for (int x = left; x < right; x += stepX)
                {
                    if (cellOwner.ContainsKey(x + "," + y)) continue;
                    POINT p;
                    p.X = x;
                    p.Y = y;
                    return p;
                }
            }

            return none;
        }

        private static bool CellBusy(List<KeyValuePair<string, ProbeRec>> all, string self, int x, int y, int stepX, int stepY)
        {
            foreach (KeyValuePair<string, ProbeRec> kv in all)
            {
                if (string.Equals(kv.Key, self, StringComparison.OrdinalIgnoreCase)) continue;
                if (Math.Abs(kv.Value.x - x) < stepX && Math.Abs(kv.Value.y - y) < stepY) return true;
            }
            return false;
        }

        private static RestoreResult RestoreSoft(Snapshot snap)
        {
            RestoreResult res = new RestoreResult();
            if (snap == null || snap.items == null) return res;
            res.totalInSnapshot = snap.items.Count;

            System.Drawing.Rectangle vs = SystemInformation.VirtualScreen;

            double sx = 1.0;
            double sy = 1.0;
            bool rescaled = false;
            if (snap.vsWidth > 0 && snap.vsHeight > 0 &&
                (snap.vsWidth != vs.Width || snap.vsHeight != vs.Height))
            {
                sx = (double)vs.Width / (double)snap.vsWidth;
                sy = (double)vs.Height / (double)snap.vsHeight;
                rescaled = true;
            }
            res.rescaled = rescaled;

            Dictionary<string, POINT> target = new Dictionary<string, POINT>(StringComparer.OrdinalIgnoreCase);
            List<string> names = new List<string>();

            foreach (SnapItem it in snap.items)
            {
                int x;
                int y;
                if (rescaled)
                {
                    x = vs.Left + (int)Math.Round((it.x - snap.vsLeft) * sx);
                    y = vs.Top + (int)Math.Round((it.y - snap.vsTop) * sy);
                }
                else
                {
                    x = it.x;
                    y = it.y;
                }

                if (x < vs.Left) x = vs.Left;
                if (y < vs.Top) y = vs.Top;
                if (x > vs.Right - 8) x = vs.Right - 8;
                if (y > vs.Bottom - 8) y = vs.Bottom - 8;

                POINT pt;
                pt.X = x;
                pt.Y = y;

                if (!target.ContainsKey(it.name))
                {
                    target[it.name] = pt;
                    names.Add(it.name);
                }
            }

            IntPtr listView = GetDesktopListView();
            if (listView == IntPtr.Zero)
            {
                res.hresult = -1;
                return res;
            }

            int stepX = snap.spacingX > 20 ? snap.spacingX : 75;
            int stepY = snap.spacingY > 20 ? snap.spacingY : 100;

            Dictionary<string, ProbeRec> live = ProbeInChildProcess();
            if (live.Count == 0)
            {
                res.hresult = -2;
                return res;
            }

            List<KeyValuePair<string, ProbeRec>> all = new List<KeyValuePair<string, ProbeRec>>(live);

            foreach (string nm in names)
            {
                if (!live.ContainsKey(nm))
                {
                    res.missing++;
                    if (res.missingNames.Count < 32) res.missingNames.Add(ShortName(nm));
                }
            }
            foreach (string nm in live.Keys)
            {
                if (!target.ContainsKey(nm))
                {
                    res.extra++;
                    if (res.extraNames.Count < 32) res.extraNames.Add(ShortName(nm));
                }
            }

            // 先把所有待移动项搬到副屏（UU 远程那张虚拟屏在主屏左侧，0 基 x < 主屏左边界），
            // 主屏上不留临时位，避免腾挪时跟真实图标抢位。
            int primaryLeft0 = -1;
            foreach (Screen sc in Screen.AllScreens)
            {
                if (sc.Primary) primaryLeft0 = sc.Bounds.Left - vs.Left;
            }
            if (primaryLeft0 <= 16) primaryLeft0 = (int)(vs.Width * 0.45);

            int parkX = 8;
            int parkRight = primaryLeft0 - 8;
            if (parkRight - parkX < stepX * 4)
            {
                parkX = 8;
                parkRight = (int)(vs.Width * 0.45);
            }

            {
                int cursorY = (int)(vs.Top + stepY);
                int cursorX = parkX;
                int parked = 0;

                foreach (string nm in names)
                {
                    ProbeRec rec;
                    if (!live.TryGetValue(nm, out rec)) continue;
                    POINT tp = target[nm];
                    if (Math.Abs(rec.x - tp.X) <= 2 && Math.Abs(rec.y - tp.Y) <= 2) continue;

                    bool placed = false;
                    for (int tries = 0; tries < 4000 && !placed; tries++)
                    {
                        if (cursorX > parkRight - stepX)
                        {
                            cursorX = parkX;
                            cursorY += stepY;
                        }
                        if (cursorY > vs.Bottom - 8) break;

                        if (!CellBusy(all, nm, cursorX, cursorY, stepX, stepY))
                        {
                            LvmSetPos(listView, rec.idx, cursorX, cursorY);
                            parked++;
                            placed = true;
                            for (int i = 0; i < all.Count; i++)
                            {
                                if (string.Equals(all[i].Key, nm, StringComparison.OrdinalIgnoreCase))
                                {
                                    ProbeRec nr = new ProbeRec();
                                    nr.idx = all[i].Value.idx;
                                    nr.x = cursorX;
                                    nr.y = cursorY;
                                    all[i] = new KeyValuePair<string, ProbeRec>(all[i].Key, nr);
                                    break;
                                }
                            }
                            Thread.Sleep(90);
                        }
                        cursorX += stepX;
                    }
                }

                Thread.Sleep(500);
            }

            // 再按快照坐标逐项放回；目标位置被别的图标占着就跳过，多跑几遍收敛
            for (int pass = 0; pass < 6; pass++)
            {
                int movedThisPass = 0;
                foreach (string nm in names)
                {
                    ProbeRec rec;
                    if (!live.TryGetValue(nm, out rec)) continue;
                    POINT tp = target[nm];
                    if (Math.Abs(rec.x - tp.X) <= 2 && Math.Abs(rec.y - tp.Y) <= 2) continue;

                    if (CellBusy(all, nm, tp.X, tp.Y, stepX, stepY)) continue;

                    LvmSetPos(listView, rec.idx, tp.X, tp.Y);
                    movedThisPass++;

                    for (int i = 0; i < all.Count; i++)
                    {
                        if (string.Equals(all[i].Key, nm, StringComparison.OrdinalIgnoreCase))
                        {
                            ProbeRec nr = new ProbeRec();
                            nr.idx = all[i].Value.idx;
                            nr.x = tp.X;
                            nr.y = tp.Y;
                            all[i] = new KeyValuePair<string, ProbeRec>(all[i].Key, nr);
                            break;
                        }
                    }

                    Thread.Sleep(80);
                }

                Thread.Sleep(250);
                if (movedThisPass == 0) break;
            }

            Thread.Sleep(400);

            Dictionary<string, ProbeRec> finalLive = ProbeInChildProcess();
            int ok = 0;
            foreach (string nm in names)
            {
                ProbeRec rec;
                if (!finalLive.TryGetValue(nm, out rec)) continue;
                POINT tp = target[nm];
                if (Math.Abs(rec.x - tp.X) <= 2 && Math.Abs(rec.y - tp.Y) <= 2) ok++;
            }

            res.exact = ok;
            res.placed = ok;
            res.hresult = 0;
            return res;
        }

        private static Dictionary<string, POINT> SnapshotPositions(IFolderView fv)
        {
            Dictionary<string, POINT> map = new Dictionary<string, POINT>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            try
            {
                fv.ItemCount(0, out count);
            }
            catch
            {
                return map;
            }

            for (int i = 0; i < count; i++)
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    fv.Item(i, out pidl);
                    if (pidl == IntPtr.Zero) continue;

                    string parsing;
                    if (!TryGetParsingName(pidl, out parsing)) continue;
                    if (string.IsNullOrEmpty(parsing)) continue;

                    POINT pt;
                    fv.GetItemPosition(pidl, out pt);
                    map[parsing] = pt;
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                    {
                        try { Native.ILFree(pidl); }
                        catch { }
                    }
                }
            }

            return map;
        }

        private static bool TryGetParsingName(IntPtr pidl, out string name)
        {
            name = null;
            IntPtr ptr = IntPtr.Zero;
            int hr = Native.SHGetNameFromIDList(pidl, Native.SIGDN_DESKTOPABSOLUTEPARSING, out ptr);
            if (hr != 0 || ptr == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                name = Marshal.PtrToStringUni(ptr);
                return !string.IsNullOrEmpty(name);
            }
            finally
            {
                try { Marshal.FreeCoTaskMem(ptr); }
                catch { }
            }
        }

        private static int ReadIconSize()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Bags\1\Desktop"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("IconSize");
                        if (v != null) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
            }
            return 48;
        }

        private static string Signature()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Screen.AllScreens.Length).Append(";");
            foreach (Screen sc in Screen.AllScreens)
            {
                sb.Append(sc.DeviceName).Append("=").Append(sc.Bounds.Width).Append("x").Append(sc.Bounds.Height).Append("@")
                  .Append(sc.Bounds.Left).Append(",").Append(sc.Bounds.Top).Append(";");
            }
            System.Drawing.Rectangle wa = SystemInformation.WorkingArea;
            sb.Append("W").Append(wa.Width).Append("x").Append(wa.Height);
            return sb.ToString();
        }

        private static string ShortName(string parsing)
        {
            if (string.IsNullOrEmpty(parsing)) return "";
            int i = parsing.LastIndexOf('\\');
            if (i >= 0 && i + 1 < parsing.Length) return parsing.Substring(i + 1);
            return parsing;
        }

        private static string JoinLimited(List<string> list, int max)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < max; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(list[i]);
            }
            if (list.Count > max) sb.Append(" …共 ").Append(list.Count).Append(" 项");
            return sb.ToString();
        }

        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            int len = 0;
            foreach (char c in s)
            {
                len += (c > 0x2E80) ? 2 : 1;
            }
            StringBuilder sb = new StringBuilder(s);
            while (len < width)
            {
                sb.Append(' ');
                len++;
            }
            return sb.ToString();
        }

        private static string Sanitize(string name)
        {
            if (name == null) return "main";
            name = name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            if (name.Length == 0) name = "main";
            if (name.Length > 60) name = name.Substring(0, 60);
            return name;
        }

        private static string LayoutPath(string name)
        {
            return Path.Combine(LayoutDir, name + ".json");
        }

        private static List<string> ListNames()
        {
            List<string> names = new List<string>();
            if (!Directory.Exists(LayoutDir)) return names;

            foreach (string f in Directory.GetFiles(LayoutDir, "*.json"))
            {
                string n = Path.GetFileNameWithoutExtension(f);
                if (n.StartsWith("_")) continue;
                names.Add(n);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static void WriteSnapshot(string path, Snapshot s)
        {
            string json = Json.Serialize(s);
            File.WriteAllText(path, PrettyJson(json), new UTF8Encoding(false));
        }

        private static Snapshot ReadSnapshot(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            Snapshot s = Json.Deserialize<Snapshot>(text);
            if (s == null) throw new InvalidDataException("快照文件内容无效：" + path);
            if (s.items == null) s.items = new List<SnapItem>();
            return s;
        }

        private static string PrettyJson(string json)
        {
            StringBuilder sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            bool escape = false;

            foreach (char c in json)
            {
                if (inString)
                {
                    sb.Append(c);
                    if (escape) { escape = false; }
                    else if (c == '\\') { escape = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        depth++;
                        sb.Append(c);
                        sb.Append('\n');
                        sb.Append(' ', depth * 2);
                        break;
                    case '}':
                    case ']':
                        depth--;
                        if (depth < 0) depth = 0;
                        sb.Append('\n');
                        sb.Append(' ', depth * 2);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        sb.Append('\n');
                        sb.Append(' ', depth * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        private static void PrintHelp()
        {
            Console.WriteLine("DeskPin 1.0  —  桌面图标布局快照 / 一键还原");
            Console.WriteLine();
            Console.WriteLine("用法：");
            Console.WriteLine("  DeskPin.exe save <名称>               保存当前桌面摆位");
            Console.WriteLine("  DeskPin.exe restore <名称>            还原到指定快照");
            Console.WriteLine("  DeskPin.exe list                      查看所有快照");
            Console.WriteLine("  DeskPin.exe rename <旧名> <新名>      重命名快照");
            Console.WriteLine("  DeskPin.exe delete <名称>             删除快照（移到 _deleted 备份）");
            Console.WriteLine("  DeskPin.exe watch [名称] [--interval 毫秒]");
            Console.WriteLine("                                        常驻：显示配置变化后自动还原（默认 main）");
            Console.WriteLine("  DeskPin.exe status                    当前屏幕 / 图标数 / 自动排列状态");
            Console.WriteLine("  DeskPin.exe where                     快照目录路径");
            Console.WriteLine();
            Console.WriteLine("示例：");
            Console.WriteLine("  DeskPin.exe save main                 摆好的日常布局存成 main");
            Console.WriteLine("  DeskPin.exe save 游戏布局              自定义中文名称也能用");
            Console.WriteLine("  DeskPin.exe restore 游戏布局           一键切回去");
            Console.WriteLine();
            Console.WriteLine("说明：");
            Console.WriteLine("  · 增删文件不影响还原：快照里有、现在没有的跳过并列出；");
            Console.WriteLine("    现在有、快照里没有的保持原位，不参与定位，不报错。");
            Console.WriteLine("  · 分辨率/任务栏高度变了也能还原：按虚拟屏比例缩放并夹取到可见区域。");
            Console.WriteLine("  · 必须关闭「自动排列图标」（右键桌面 → 查看），否则系统会立刻抹掉位置。");
            Console.WriteLine("  · 快照目录：" + LayoutDir);
        }

        #endregion
    }
}
