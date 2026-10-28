using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// CadImeLite v6 —— 中/英切换做成三条通道，按输入法能力自动选：
//   ① tsf   ：TSF compartment（GUID 由 rules.ini 的 tsf_guid= 指定，读不到值就跳过）
//   ② imm32 ：WM_IME_CONTROL 设转换模式（微软拼音实测有效，必须保留作回退）
//   ③ shift ：合成一次 Shift（微信输入法/搜狗这类纯 TSF 输入法只认自己的热键，
//              IMM32 调用返回 OK 但实际不动，只能靠按键；读数无法校验，日志会写明）
//
// 与旧 CadImeSwitch 的根本区别：
//   旧：英文 = 切到英文布局 00000409 -> 窗口变成「没有输入法」，Shift 也救不回来
//   v5：布局永远是 00000804（本机唯一布局），中/英 = IME_CMODE_NATIVE 位
//   v6：同一套判据，只换「怎么落地」，并在日志里标出这次用的是哪条通道
//
// 作用范围：只处理 rules.ini 里 scope= 指定的进程（默认 acad），
//           其他应用（记事本/微信/飞秋/浏览器）一律不碰。
class CadImeLite
{
    // ---------- Win32 ----------
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gti);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int l, t, r, b;
    }

    const int WM_IME_CONTROL = 0x0283;
    const int IMC_GETCONVERSIONMODE = 0x0001;
    const int IMC_SETCONVERSIONMODE = 0x0002;
    const int IMC_GETOPENSTATUS = 0x0005;
    const int IMC_SETOPENSTATUS = 0x0006;
    const int IME_CMODE_NATIVE = 0x0001;

    // ---------- v6 通道配置 ----------
    // channel=auto   ：先 tsf（配了 GUID 且读得到）→ imm32（能读回才算有效）→ shift
    // channel=imm32  ：强制 IMM32（主机上的微软拼音）
    // channel=shift  ：强制合成 Shift（微信输入法/搜狗）
    // channel=tsf    ：强制 TSF（tsf_guid 必须配）
    static string _channel = "auto";
    static Guid _tsfGuid; static bool _tsfGuidSet;

    // ---------- 状态 ----------
    static string _dir, _logPath;
    static readonly Dictionary<string, int> _rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>类名前缀规则（rules.ini 里写成 `HwndWrapper[DefaultDomain*=0`）。类名带 GUID 的只能这样认。</summary>
    static readonly List<KeyValuePair<string, int>> _prefixRules = new List<KeyValuePair<string, int>>();

    static readonly List<string> _scope = new List<string>();
    static bool _paused;
    static IntPtr _lastTarget = IntPtr.Zero;
    static int _lastWant = -1;
    static DateTime _lastAct = DateTime.MinValue;
    // v5：离开复位去抖（同一窗口 1 秒内不重复复位）
    static IntPtr _lastResetTarget = IntPtr.Zero;
    static DateTime _lastResetAt = DateTime.MinValue;

    static void Log(string s)
    {
        try
        {
            File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + PidTag + s + "\r\n", Encoding.UTF8);
            var fi = new FileInfo(_logPath);
            if (fi.Exists && fi.Length > 400000)
            {
                var t = File.ReadAllText(_logPath, Encoding.UTF8);
                File.WriteAllText(_logPath, t.Substring(t.Length / 2), Encoding.UTF8);
            }
        }
        catch { }
    }

    // v6.4：每条日志带进程号，多实例同时写日志时一眼能分开
    static readonly string PidTag = "[pid:" + System.Diagnostics.Process.GetCurrentProcess().Id + "] ";

    static System.Threading.Mutex _singleMutex;

    /// <summary>
    /// v6.4 单实例保护：全局命名互斥体拿不到就静默退出（只写一行日志，不弹窗）。
    /// 先试 Global\（跨会话），没权限再退到 Local\（本会话）。
    /// </summary>
    static bool EnsureSingleInstance()
    {
        string[] names = new string[] { @"Global\CadImeLite.SingleInstance", @"Local\CadImeLite.SingleInstance" };
        for (int i = 0; i < names.Length; i++)
        {
            string n = names[i];
            try
            {
                bool created;
                _singleMutex = new System.Threading.Mutex(true, n, out created);
                if (!created)
                {
                    Log("已有实例在跑（互斥体 " + n + "），本进程退出");
                    try { _singleMutex.Dispose(); } catch { }
                    _singleMutex = null;
                    return false;
                }
                Log("单实例保护 OK，互斥体=" + n);
                return true;
            }
            catch (UnauthorizedAccessException) { continue; }   // Global\ 要权限，换 Local\
            catch (Exception ex) { Log("建互斥体异常 " + n + "：" + ex.Message); continue; }
        }
        Log("单实例保护没建成（互斥体都创建失败），继续运行");
        return true;
    }

    static string Cls(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string ProcName(IntPtr h)
    {
        try
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return "";
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) return p.ProcessName;
        }
        catch { return ""; }
    }

    static void LoadRules()
    {
        string path = Path.Combine(_dir, "rules.ini");
        var klass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k.Equals("scope", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        _scope.Add(s.Trim());
                    continue;
                }
                if (k.Equals("channel", StringComparison.OrdinalIgnoreCase))
                {
                    string cv = v.ToLowerInvariant();
                    if (cv == "auto" || cv == "tsf" || cv == "imm32" || cv == "shift" || cv == "conv" || cv == "open") _channel = cv;
                    continue;
                }
                if (k.Equals("tsf_guid", StringComparison.OrdinalIgnoreCase))
                {
                    Guid tg;
                    if (Guid.TryParse(v.Trim(), out tg)) { _tsfGuid = tg; _tsfGuidSet = true; }
                    continue;
                }
                int n; if (!int.TryParse(v, out n)) continue;
                if (k.EndsWith("*"))                       // 前缀规则：HwndWrapper[DefaultDomain*=0
                {
                    string p = k.Substring(0, k.Length - 1).Trim();
                    if (p.Length > 0) _prefixRules.Add(new KeyValuePair<string, int>(p, n));
                }
                else klass[k] = n;
            }
        }
        if (_scope.Count == 0) _scope.Add("acad");
        foreach (var kv in klass) _rules[kv.Key] = kv.Value;
        // 内置前缀：CAD 2024 的命令行是 WPF 窗口，类名 HwndWrapper[DefaultDomain;;<GUID>，GUID 每次启动都变
        if (_prefixRules.Count == 0)
            _prefixRules.Add(new KeyValuePair<string, int>("HwndWrapper[DefaultDomain", 0));
        Log("规则 " + _rules.Count + " 条, 前缀规则 " + _prefixRules.Count + " 条, scope=" + string.Join(",", _scope.ToArray())
            + " 通道=" + _channel + (_tsfGuidSet ? (" tsf_guid=" + _tsfGuid.ToString("D")) : " tsf_guid=未配置"));
    }

    /// <summary>读 IME 转换模式；-1 = 这个窗口拿不到 IME 窗口（CAD 常见）。</summary>
    static int GetConv(IntPtr target)
    {
        IntPtr ime = ImmGetDefaultIMEWnd(target);
        if (ime == IntPtr.Zero) return -1;
        long v = SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero).ToInt64();
        return (int)(v & 0xFFFF);
    }

    static int GetOpen(IntPtr target)
    {
        IntPtr ime = ImmGetDefaultIMEWnd(target);
        if (ime == IntPtr.Zero) return -1;
        return (int)SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero).ToInt64();
    }

    /// <summary>设置中(chinese=true)/英(false)：只改 IME_CMODE_NATIVE 位，其余位保留。</summary>
    static bool SetChinese(IntPtr target, bool chinese, out int before, out int after)
    {
        before = GetConv(target); after = before;
        if (before < 0) return false;                    // 没有 IME 窗口，别硬来
        IntPtr ime = ImmGetDefaultIMEWnd(target);

        // 输入法保持打开：中/英都靠转换模式表达，这样 Shift 在两种状态下都还能用
        if (GetOpen(target) == 0)
            SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)1);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int conv = GetConv(target);
            if (conv < 0) return false;
            bool isCn = (conv & IME_CMODE_NATIVE) != 0;
            if (isCn == chinese) { after = conv; return true; }

            // 实测（probe4, 2026-10-24）：微软拼音只认「整值」，不接受只清某一位。
            //   SET 0x0001 -> 回读 0x0001（中文）
            //   SET 0x0000 -> 回读 0x0000（英文）
            //   SET 0x0400（想保留 SYMBOL 位、只清 NATIVE）-> 回读仍是 0x0401，**不生效**
            // 所以这里直接写 1 / 0，写完回读校验。
            int want = chinese ? 0x0001 : 0x0000;
            SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETCONVERSIONMODE, (IntPtr)want);
            System.Threading.Thread.Sleep(55);
            after = GetConv(target);
            if (after >= 0 && ((after & IME_CMODE_NATIVE) != 0) == chinese) return true;
            System.Threading.Thread.Sleep(55);
        }
        return false;
    }

    // ================= v6：三条通道 =================
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, IntPtr inputs, int size);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

    // ================= v6.5：托盘图标画「中 / 英」=================
    static NotifyIcon _tray = null;
    static string _trayTag = "";

    /// <summary>画托盘图标：色块 + 白字「中」或「英」（红=中文，蓝=英文）。</summary>
    static Icon MakeStateIcon(string tag, Color back)
    {
        const int size = 32;
        using (var bmp = new Bitmap(size, size))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(back);
                using (var f = new Font("Microsoft YaHei", 22f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var br = new SolidBrush(Color.White))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(tag, f, br, new RectangleF(0, 0, size, size), sf);
                }
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
            }
            finally { DestroyIcon(h); }
        }
    }

    static string FitText(string s) { return s.Length <= 63 ? s : s.Substring(0, 63); }

    /// <summary>更新托盘图标状态。chinese=true 显示「中」（红），false 显示「英」（蓝）。</summary>
    static void SetTrayState(bool chinese, string note)
    {
        if (_tray == null) return;
        string tag = chinese ? "中" : "英";
        string tip = FitText("CadImeLite · " + tag + (string.IsNullOrEmpty(note) ? "" : ("（" + note + "）")));
        try
        {
            if (tag != _trayTag)
            {
                _trayTag = tag;
                Icon ic = MakeStateIcon(tag, chinese ? Color.FromArgb(0xC0, 0x20, 0x20) : Color.FromArgb(0x20, 0x60, 0xC0));
                Icon old = _tray.Icon;
                _tray.Icon = ic;
                try
                {
                    if (old != null && !object.ReferenceEquals(old, ic) && !object.ReferenceEquals(old, SystemIcons.Application)) old.Dispose();
                }
                catch { }
            }
            _tray.Text = tip;
        }
        catch { }
    }

    delegate int D2(IntPtr self, out int a);
    delegate int D2P(IntPtr self, out IntPtr p);
    delegate int DGetComp(IntPtr self, ref Guid g, out IntPtr pp);
    delegate int DSetVal(IntPtr self, int tid, IntPtr pvar);
    delegate int DGetVal(IntPtr self, IntPtr pvar);

    static IntPtr _tsfTm = IntPtr.Zero, _tsfGcm = IntPtr.Zero;
    static int _tsfTid = 1;
    static bool _tsfSetFirst = true, _tsfOrderOk, _tsfTried;
    static string _lastChannel = "";
    static string _lastIme = "";
    static readonly Dictionary<IntPtr, bool> _convOk = new Dictionary<IntPtr, bool>();

    static IntPtr Slot(IntPtr p, int i)
    {
        IntPtr vt = Marshal.ReadIntPtr(p);
        return Marshal.ReadIntPtr(vt, i * IntPtr.Size);
    }

    static T Fn<T>(IntPtr p, int i) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(p, i), typeof(T));
    }

    /// <summary>初始化 TSF：线程管理器 → 全局 compartment 管理器，并用随机 GUID 自检 Set/Get 的槽位。</summary>
    static bool TsfInit()
    {
        if (_tsfTried) return _tsfGcm != IntPtr.Zero;
        _tsfTried = true;
        try
        {
            Guid clsid = new Guid("529A9E6B-6587-4F23-AB9E-9C7D683E3C50"); // CLSID_TF_ThreadMgr
            Guid iid = new Guid("AA80E801-2021-11D2-93E0-0060B067B86E");   // IID_ITfThreadMgr
            IntPtr pp;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out pp);
            if (hr != 0 || pp == IntPtr.Zero) { Log("tsf 初始化失败 hr=0x" + hr.ToString("X8")); return false; }
            _tsfTm = pp;
            int tid;
            hr = Fn<D2>(_tsfTm, 3)(_tsfTm, out tid);
            if (hr == 0) _tsfTid = tid;
            IntPtr gcm;
            hr = Fn<D2P>(_tsfTm, 13)(_tsfTm, out gcm);
            if (hr != 0 || gcm == IntPtr.Zero) { Log("tsf 取全局 compartment 失败 hr=0x" + hr.ToString("X8")); return false; }
            _tsfGcm = gcm;

            IntPtr c = TsfComp(Guid.NewGuid());
            int v;
            if (c != IntPtr.Zero)
            {
                TsfWriteInt(c, 777); TsfReadInt(c, out v);
                if (v == 777) { _tsfSetFirst = true; _tsfOrderOk = true; }
                else
                {
                    _tsfSetFirst = false;
                    TsfWriteInt(c, 778); TsfReadInt(c, out v);
                    _tsfOrderOk = (v == 778);
                }
            }
            Log("tsf 就绪 槽位自检=" + (_tsfOrderOk ? "通过" : "可疑"));
            return true;
        }
        catch (Exception ex) { Log("tsf 异常: " + ex.Message); return false; }
    }

    static IntPtr TsfComp(Guid g)
    {
        try
        {
            if (_tsfGcm == IntPtr.Zero) return IntPtr.Zero;
            IntPtr p;
            return Fn<DGetComp>(_tsfGcm, 3)(_tsfGcm, ref g, out p) == 0 ? p : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    static int TsfReadInt(IntPtr comp, out int val)
    {
        val = -1;
        if (comp == IntPtr.Zero) return -1;
        IntPtr pv = Marshal.AllocHGlobal(64);
        try
        {
            for (int i = 0; i < 64; i++) Marshal.WriteByte(pv, i, 0);
            int hr = _tsfSetFirst ? Fn<DGetVal>(comp, 4)(comp, pv) : Fn<DGetVal>(comp, 3)(comp, pv);
            short vt = Marshal.ReadInt16(pv, 0);
            int v = Marshal.ReadInt32(pv, 8);
            if (hr != 0) return hr;                 // S_FALSE(1) = 外部看不到值
            if (vt == 3) { val = v; return 0; }     // VT_I4
            if (vt == 11) { val = v == 0 ? 0 : 1; return 0; } // VT_BOOL
            val = -2; return 0;
        }
        catch { return -1; }
        finally { Marshal.FreeHGlobal(pv); }
    }

    static int TsfWriteInt(IntPtr comp, int v)
    {
        if (comp == IntPtr.Zero) return -1;
        IntPtr pv = Marshal.AllocHGlobal(64);
        try
        {
            for (int i = 0; i < 64; i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 3);           // VT_I4
            Marshal.WriteInt32(pv, 8, v);
            return _tsfSetFirst ? Fn<DSetVal>(comp, 3)(comp, _tsfTid, pv) : Fn<DSetVal>(comp, 4)(comp, _tsfTid, pv);
        }
        catch { return -1; }
        finally { Marshal.FreeHGlobal(pv); }
    }

    static bool TsfRead(Guid g, out int val)
    {
        val = -1;
        IntPtr c = TsfComp(g);
        if (c == IntPtr.Zero) return false;
        if (TsfReadInt(c, out val) != 0) return false;
        return val != -2;
    }

    /// <summary>转换模式通道对这个窗口是否真的有效（v6.1 起不再用于选通道，仅保留给自测参考）。</summary>
    static bool ConvWorks(IntPtr target)
    {
        bool cached;
        if (_convOk.TryGetValue(target, out cached)) return cached;
        int conv = GetConv(target);
        bool ok;
        if (conv < 0) ok = false;
        else if ((conv & IME_CMODE_NATIVE) != 0) ok = true;
        else
        {
            IntPtr ime = ImmGetDefaultIMEWnd(target);
            SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_SETCONVERSIONMODE, (IntPtr)1);
            System.Threading.Thread.Sleep(70);
            int again = GetConv(target);
            ok = again >= 0 && (again & IME_CMODE_NATIVE) != 0;
        }
        _convOk[target] = ok;
        Log("通道自检 窗口=0x" + target.ToInt64().ToString("X") + " 转换模式=" + (ok ? "有效" : "无效(设中文读不回来)"));
        return ok;
    }

    /// <summary>合成一次 Shift（按下+抬起）。SendInput 的返回值要核对，不能只看调用没抛异常。</summary>
    static bool SendShift()
    {
        int sz = IntPtr.Size == 8 ? 40 : 28;
        IntPtr p = Marshal.AllocHGlobal(sz * 2);
        try
        {
            for (int i = 0; i < sz * 2; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, 1);                    // INPUT_KEYBOARD
            Marshal.WriteInt16(p, 8, 0x10);                 // VK_SHIFT
            Marshal.WriteInt16(p, 10, 0x2A);                // scancode
            Marshal.WriteInt32(p, sz, 1);                   // 第二条：抬起
            Marshal.WriteInt16(p, sz + 8, 0x10);
            Marshal.WriteInt16(p, sz + 10, 0x2A);
            Marshal.WriteInt32(p, sz + 12, 2);              // KEYEVENTF_KEYUP
            uint r = SendInput(2, p, sz);
            if (r != 2) Log("shift SendInput 返回 " + r + " err=" + Marshal.GetLastWin32Error());
            return r == 2;
        }
        catch (Exception ex) { Log("shift 注入异常: " + ex.Message); return false; }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// 落地一次中/英切换：tsf → imm32 → shift。
    /// 返回 true 表示动作已发出；channel 输出实际用的通道，日志必须能区分。
    /// </summary>
    static bool Switch(IntPtr target, bool chinese, out int before, out int after, out string channel)
    {
        int want = chinese ? 1 : 0;
        before = GetConv(target); after = before;
        channel = "imm32conv";

        // ---- 通道选择（auto 按输入法身份判定，不靠"设了再读回"试错）----
        string ime = "";
        bool allowConv = true, allowOpen = true, allowShift = true;
        if (_channel == "auto")
        {
            IntPtr fgW = GetForegroundWindow();
            ime = DetectIme(fgW);
            if (ime.Length > 0 && !IsMsPinyin(ime))
            {
                // 第三方中文输入法（微信/搜狗/QQ拼音…）：只有 open 通道实测有效
                allowConv = false; allowShift = false;
            }
            else
            {
                // 微软拼音，或认不出来：一律信转换模式。
                // 2026-10-28 主机实测：#32770 对话框里"设中文读不回来"会误判无效，
                // 一旦据此切 open 就等于把微软拼音关掉（打不出中文、Shift 也没反应）。
                // 所以不再做"某个窗口自检失败就换通道"的判决。
                allowOpen = false; allowShift = false;
            }
            LogImeOnce(fgW, ime);
        }
        else if (_channel == "conv" || _channel == "imm32") { allowOpen = false; allowShift = false; }
        else if (_channel == "open") { allowConv = false; allowShift = false; }
        else if (_channel == "shift") { allowConv = false; allowOpen = false; }
        _lastIme = ime;

        // ① TSF：只有配了 GUID 且能读回值才走
        if ((_channel == "auto" || _channel == "tsf") && _tsfGuidSet && TsfInit())
        {
            int v;
            if (TsfRead(_tsfGuid, out v))
            {
                before = v;
                if (v == want) { after = v; channel = "tsf"; return true; }
                IntPtr comp = TsfComp(_tsfGuid);
                TsfWriteInt(comp, want);
                System.Threading.Thread.Sleep(80);
                int v2;
                if (TsfRead(_tsfGuid, out v2))
                {
                    after = v2;
                    if (v2 == want) { channel = "tsf"; return true; }
                }
                Log("通道=tsf 写 " + want + " 未生效(读回 " + v2 + ")，降级");
            }
        }

        // ② 转换模式（微软拼音走的通道）
        //    不再做"设了再读回"的通道判决：主机实测 #32770 对话框里读不回来，
        //    但那是窗口特性，不代表整台机器的 conv 无效。
        if (allowConv)
        {
            int b, a;
            if (SetChinese(target, chinese, out b, out a)) { before = b; after = a; channel = "imm32conv"; return true; }
            before = b; after = a;
            Log("通道=imm32conv 未生效 conv 0x" + Math.Max(b, 0).ToString("X4") + "->0x" + Math.Max(a, 0).ToString("X4") + "（本窗口读不回来，不换通道）");
        }

        // ③ 输入法开/关（微信输入法 / 搜狗：转换模式无效，但 IMC_SETOPENSTATUS 实测有效）
        //    关 = 英文（字母直接进程序），开 = 中文（字母进输入法组合）。
        //    2026-10-28 在 CAD 命令行实测：关后敲 abc 出 abc，开后敲 xyz 不出拉丁字母，读回与设置一致。
        if (allowOpen)
        {
            IntPtr imeWnd = ImmGetDefaultIMEWnd(target);
            if (imeWnd != IntPtr.Zero)
            {
                int ob = GetOpen(target);
                SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)want);
                System.Threading.Thread.Sleep(140);
                int oa = GetOpen(target);
                before = ob; after = oa;
                if (oa == want) { channel = "imm32open"; return true; }
                channel = "imm32open";   // 失败也要如实标通道，别在日志里显示成 imm32conv
                Log("通道=imm32open 未生效 开 " + ob + "->" + oa + " 目标=" + want + "，降级");
            }
        }
        if (!allowShift) return false;

        // ③ 合成 Shift：只能在目标进程还是前台时发，避免把 Shift 丢给别的应用
        IntPtr fg = GetForegroundWindow();
        if (!ProcName(fg).Equals(ProcName(target), StringComparison.OrdinalIgnoreCase))
        {
            Log("通道=shift 跳过：前台已不是目标进程(前台=" + ProcName(fg) + ")");
            channel = "shift";
            return false;
        }
        before = GetConv(target);
        bool sent = SendShift();
        System.Threading.Thread.Sleep(120);
        after = GetConv(target);
        channel = "shift";
        return sent;
    }

    /// <summary>进程名是否在 scope 名单里。</summary>
    static bool InScopeName(string proc)
    {
        for (int i = 0; i < _scope.Count; i++)
            if (string.Equals(_scope[i], proc, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 用户手按 Shift 的检测（WH_KEYBOARD_LL）。shift 通道是「盲切」：发一次 Shift 就翻转，
    /// 用户自己按 Shift 后程序不知道，下一次切换就会翻反。装个低级键盘钩子：
    /// 只认**非注入**的 Shift（程序自己 SendInput 发的带 LLKHF_INJECTED，跳过），
    /// 前台是 acad 时把记账翻过来，这样盲切就不会累积错误。
    /// </summary>
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static IntPtr _hook = IntPtr.Zero;
    static IntPtr _userOverride = IntPtr.Zero;   // 用户手按过 Shift 的那个窗口：本轮不再抢
    static HookProc _hookProc;   // 必须留引用，否则被 GC 回收后回调会崩

    static void InstallShiftHook()
    {
        try
        {
            _hookProc = new HookProc(delegate(int nCode, IntPtr wParam, IntPtr lParam)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    if (nCode >= 0 && (msg == 0x0100 || msg == 0x0104))   // WM_KEYDOWN / WM_SYSKEYDOWN
                    {
                        int vk = Marshal.ReadInt32(lParam, 0);
                        int flags = Marshal.ReadInt32(lParam, 8);
                        bool injected = (flags & 0x10) != 0;              // LLKHF_INJECTED
                        bool isShift = vk == 0x10 || vk == 0xA0 || vk == 0xA1;
                        if (isShift && !injected && _lastChannel == "shift")
                        {
                            IntPtr fg = GetForegroundWindow();
                            if (InScopeName(ProcName(fg)))
                            {
                                _lastWant = _lastWant == 0 ? 1 : 0;
                                _userOverride = _lastTarget;   // 这个窗口用户自己切过了，本轮别抢
                                Log("用户手按 Shift（前台=acad），记账翻转为=" + (_lastWant == 1 ? "中" : "英") + "，本窗口不再抢");
                            }
                        }
                    }
                }
                catch { }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            });
            _hook = SetWindowsHookEx(13, _hookProc, GetModuleHandle(null), 0);
            Log("Shift 钩子=" + (_hook != IntPtr.Zero ? "已装" : ("装不上 err=" + Marshal.GetLastWin32Error())));
        }
        catch (Exception ex) { Log("装 Shift 钩子异常: " + ex.Message); }
    }

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetDescription(IntPtr hkl, StringBuilder buf, int n);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] static extern int ImmGetIMEFileName(IntPtr hkl, StringBuilder buf, int n);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint tid);

    static readonly Dictionary<IntPtr, string> _imeNames = new Dictionary<IntPtr, string>();

    /// <summary>
    /// 识别前台进程当前用的是哪个输入法：先看进程里加载了哪些 IME 模块
    /// （微信输入法 = wetype_tip.dll，实测 acad.exe 里确实加载了），
    /// 再退回 IMM32 的 ImmGetDescription / ImmGetIMEFileName（对 TSF 输入法通常是空，实测 hkl=0x8040804 时 desc 为空）。
    /// 识别不出来就返回 ""，由调用方按 conv→open→shift 顺序回落，并在日志里写“auto未识别IME=”。
    /// </summary>
    static string DetectIme(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return "";
        string cached;
        if (_imeNames.TryGetValue(fg, out cached)) return cached;
        string name = "";
        try
        {
            uint pid; GetWindowThreadProcessId(fg, out pid);
            if (pid != 0)
            {
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                {
                    foreach (System.Diagnostics.ProcessModule m in p.Modules)
                    {
                        string n = m.ModuleName.ToLowerInvariant();
                        if (n.IndexOf("wetype") >= 0) { name = "微信输入法"; break; }
                        if (n.IndexOf("sogou") >= 0) { name = "搜狗输入法"; break; }
                        if (n.IndexOf("qqpy") >= 0 || n.IndexOf("qqpinyin") >= 0) { name = "QQ拼音"; break; }
                        if (n.IndexOf("baidupin") >= 0 || n.IndexOf("baiduime") >= 0) { name = "百度输入法"; break; }
                        if (n.IndexOf("weasel") >= 0 || n.IndexOf("rime") >= 0) { name = "小狼毫"; break; }
                    }
                }
            }
        }
        catch { }
        if (name.Length == 0)
        {
            try
            {
                uint pid2; GetWindowThreadProcessId(fg, out pid2);
                IntPtr hkl = GetKeyboardLayout(pid2);
                var sb = new StringBuilder(256);
                if (ImmGetDescription(hkl, sb, 256) > 0 && sb.Length > 0) name = sb.ToString();
                else
                {
                    var fb = new StringBuilder(256);
                    if (ImmGetIMEFileName(hkl, fb, 256) > 0 && fb.Length > 0) name = fb.ToString();
                }
            }
            catch { }
        }
        _imeNames[fg] = name;
        return name;
    }

    /// <summary>输入法名字是不是微软拼音（走转换模式通道）。</summary>
    static bool IsMsPinyin(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("微软", StringComparison.Ordinal) >= 0
            || name.IndexOf("Microsoft Pinyin", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string _imeLogKey = "";

    /// <summary>IME 判定结果只在变化时写一次日志（2026-10-28 主机实测：每次判据都写会把日志刷爆）。</summary>
    static void LogImeOnce(IntPtr fg, string ime)
    {
        try
        {
            uint pid; GetWindowThreadProcessId(fg, out pid);
            string key = pid.ToString() + "|" + ime;
            if (key == _imeLogKey) return;
            _imeLogKey = key;
            if (ime.Length == 0)
                Log("auto未识别IME=（pid=" + pid + " 进程模块和 IMM32 描述都读不到）→ 信转换模式 conv");
            else
                Log("auto识别 IME=" + ime + "（pid=" + pid + "）→ " + (IsMsPinyin(ime) ? "conv" : "open") + " 通道");
        }
        catch { }
    }

    /// <summary>对话框/控件类瞬态窗口：不在这些窗口上动输入法。</summary>
    static bool IsUiChrome(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        string[] bad = { "Button", "ComboBox", "msctls_statusbar32", "Static", "ListBox",
                         "#32770", "tooltips_class32", "SysListView32", "SysTreeView32", "ScrollBar" };
        for (int i = 0; i < bad.Length; i++)
            if (cls.Equals(bad[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>父链里有没有对话框（#32770）。</summary>
    static bool ChainHasDialog(IntPtr h)
    {
        IntPtr p = h;
        for (int i = 0; i < 6 && p != IntPtr.Zero; i++)
        {
            if (Cls(p).IndexOf("#32770", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            p = GetParent(p);
        }
        return false;
    }

    /// <summary>父链里有没有 Chromium/CEF 窗口（CAD 起始页、部分面板）。</summary>
    static bool ChainHasCef(IntPtr h)
    {
        IntPtr p = h;
        for (int i = 0; i < 6 && p != IntPtr.Zero; i++)
        {
            if (IsCef(Cls(p))) return true;
            p = GetParent(p);
        }
        return false;
    }

    /// <summary>父链里有没有工具栏/功能区容器（焦点落在功能按钮上时不该切输入法）。</summary>
    static bool ChainHasControlBar(IntPtr h)
    {
        IntPtr p = h;
        for (int i = 0; i < 6 && p != IntPtr.Zero; i++)
        {
            string c = Cls(p);
            if (c.IndexOf("AfxControlBar", StringComparison.OrdinalIgnoreCase) >= 0
                || c.IndexOf("AfxToolBar", StringComparison.OrdinalIgnoreCase) >= 0
                || c.IndexOf("MfcRibbon", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            p = GetParent(p);
        }
        return false;
    }

    /// <summary>设成中文后，按通道语义判断"现在确实是中文"。</summary>
    static bool IsChineseBack(string ch, int v)
    {
        if (ch == "shift") return true;          // 盲切没有读数，不再重试
        if (ch == "imm32open") return v == 1;    // 开=中文
        return v >= 0 && (v & IME_CMODE_NATIVE) != 0;
    }

    /// <summary>前台窗口的真实焦点控件（AutoCAD 的命令行/编辑器都是子窗口，焦点在子窗口上才准）。</summary>
    static IntPtr FocusOf(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        uint pid;
        uint tid = GetWindowThreadProcessId(fg, out pid);
        if (tid != 0)
        {
            var gti = new GUITHREADINFO();
            gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero) return gti.hwndFocus;
        }
        return fg;
    }

    // ---- B 方案（2026-10-26）：读 LISP 命令状态 ----
    // CadImeSwitch.lsp 在命令开始/结束时把 Line3 写成 CN / EN / IDLE。
    // 两条防串台规则：
    //   ① 第一次读到只记账、不行动；只有"看到它变化过"之后才相信，
    //      避免 CAD 上次退出时留下的残留状态把首条命令吃掉。
    //   ② 绑定"变化发生时前台是哪个 CAD 进程"，两个 CAD 同开也不会互相污染。
    //   ③ 两个小时没有任何变化就视为失效，退回窗口判据。
    static string _lspKey = null;
    static string _lspLine = "";
    static bool _lspKnown = false;
    static uint _lspPid = 0;
    static DateTime _lspSeen = DateTime.MinValue;

    static bool TryLspWant(uint pid, out int want)
    {
        want = 0;
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CadImeSwitch"))
            {
                if (k == null) return false;
                string line = (k.GetValue("Line3") as string) ?? "";
                string cmd = (k.GetValue("Command") as string) ?? "";
                if (line.Length == 0) return false;
                string key = cmd + "|" + line;
                if (_lspKey == null) { _lspKey = key; _lspLine = line; return false; }
                if (key != _lspKey)
                {
                    _lspKey = key; _lspLine = line;
                    _lspKnown = true; _lspPid = pid; _lspSeen = DateTime.Now;
                }
                // v7：不再要求"当前前台进程 == 写入进程"。
                // 两个 CAD 同开时共用一个注册表键，绑进程会导致切到另一个 CAD 就失效；
                // 代价是极端情况下会短暂沿用另一个 CAD 的状态，下一条命令即自愈。
                if (!_lspKnown) return false;
                if ((DateTime.Now - _lspSeen).TotalMinutes > 120) return false;
                if (line == "CN") { want = 1; return true; }
                if (line == "EN" || line == "IDLE") { want = 0; return true; }
            }
        }
        catch { }
        return false;
    }

    static void Tick()
    {
        if (_paused) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;

        string proc = ProcName(fg);
        bool inScope = false;
        for (int i = 0; i < _scope.Count; i++)
            if (string.Equals(_scope[i], proc, StringComparison.OrdinalIgnoreCase)) { inScope = true; break; }
        if (!inScope)
        {
            // ---- 焦点离开 AutoCAD：把刚才那个 CAD 控件复位成中文 ----
            // v5（2026-10-26）收紧。原实现只要 _lastTarget 非空就复位，结果
            // ime-lite.log 里 10:48:38~10:49:11 连刷 5 次
            // "离开acad 复位=中文 窗口=0x10C4C conv 0x0001->0x0001"——值根本没变，纯噪声。
            // 三条一起上：
            //   ① 只有**工具确实把它设成过英文**（_lastWant==0）才复位；
            //      用户自己按 Shift 改的、本来就在中文的，一律不碰；
            //   ② 复位前先读实际状态，已经是中文就跳过，不写日志；
            //   ③ 同一窗口 1 秒内不重复复位。
            if (_lastTarget != IntPtr.Zero && _lastWant == 0)
            {
                int cur = GetConv(_lastTarget);
                bool isEn = cur >= 0 && (cur & IME_CMODE_NATIVE) == 0;
                bool debounce = (_lastTarget != _lastResetTarget) ||
                                (DateTime.Now - _lastResetAt).TotalMilliseconds >= 1000;
                if (isEn && debounce)
                {
                    IntPtr rt = _lastTarget;
                    int lb, la; string lch;
                    bool lok = Switch(rt, true, out lb, out la, out lch);
                    bool ok1 = lok && IsChineseBack(lch, la);
                    if (!ok1)
                    {
                        // v6.2：复位没落地就重试一次（窗口刚失焦时输入法会短暂不响应），并写日志，不静默
                        System.Threading.Thread.Sleep(150);
                        int rb, ra; string rch;
                        bool rok = Switch(rt, true, out rb, out ra, out rch);
                        Log(string.Format("离开acad 复位=中文 未生效(重试1) 通道={0}->{1} 窗口=0x{2:X} conv 0x{3:X4}->0x{4:X4} {5}",
                            lch, rch, rt.ToInt64(), Math.Max(lb, 0), Math.Max(ra, 0),
                            (rok && IsChineseBack(rch, ra)) ? "重试成功" : "仍失败"));
                        lok = rok; la = ra; lch = rch;
                    }
                    _lastResetTarget = rt;
                    _lastResetAt = DateTime.Now;
                    Log(string.Format("离开acad 复位=中文 通道={0} 窗口=0x{1:X} conv 0x{2:X4}->0x{3:X4} {4}",
                        lch, rt.ToInt64(), Math.Max(lb, 0), Math.Max(la, 0),
                        (lok && IsChineseBack(lch, la)) ? "OK" : "未生效"));
                    if (lok && IsChineseBack(lch, la)) SetTrayState(true, "离开acad");
                }
            }
            _lastTarget = IntPtr.Zero; _lastWant = -1;
            return;
        }

        IntPtr target = FocusOf(fg);
        string cls = Cls(target);
        if (cls.Length == 0) return;

        // v6.1：对话框/控件类瞬态窗口一律不动输入法。
        // 2026-10-28 主机实测：焦点落在 #32770 对话框的 Button 上时，
        // 转换模式自检读不回来，旧逻辑据此切到 open（把微软拼音关掉）——必须堵住。
        // v6.2：Chromium/CEF 窗口一起拦。主机实测 0x90CF0 = Chrome_WidgetWin_0 > CefBrowserWindow
        //       （2018 起始页），在那上面设中文时好时坏（09:47 OK、09:48 起多次未生效）。
        if (IsUiChrome(cls) || IsCef(cls) || ChainHasCef(target) || ChainHasDialog(target) || ChainHasControlBar(target)) return;

        // ---- 判定规则（v5，2026-10-26：绘图区改成英文）----
        //
        // v4 的规则是「父链含 MDIClient（绘图区）→ 一律中文」。
        // 2026-10-26 实机问题：AutoCAD 里**焦点在绘图区时敲字母也会进命令行**，
        // 于是从多行文字退出后（焦点回到绘图区=中文），敲命令的字母先被中文输入法吃掉，
        // 表现为"卡一下 / 输不进去"，日志里能看到中英每 0~2 秒来回跳。
        //
        // 现在改为：绘图区 → **英文**（命令优先）。
        // 代价：多行文字/文字编辑里要按一次 Shift 切中文（老方案的已知代价）。
        // 后续 B 方案：接 LISP 命令状态（MTEXT/TEXT 时中文），即可两全。
        string chain;
        int want;
        string hitPrefix = null;
        bool inDraw = InDrawArea(target, out chain);
        bool found = false;

        // B 方案优先：LISP 知道"现在在不在文字命令里"，比窗口类名准
        uint fgPid; GetWindowThreadProcessId(fg, out fgPid);
        if (TryLspWant(fgPid, out want)) { found = true; hitPrefix = "LSP"; }
        else
        {
            found = inDraw;
            if (found) want = 0;
            else
            {
            // ② 类名前缀规则（第三轮加）：CAD 2024 的命令行是 WPF 窗口，
            //    类名 HwndWrapper[DefaultDomain;;<GUID>]，GUID 每次启动都变，只能按前缀认。
            //    优先级低于绘图区（上面已判），高于下面的精确类名表。
            found = PrefixWant(target, out want, out hitPrefix);
            if (!found) found = _rules.TryGetValue(cls, out want);
            if (!found)
            {
                IntPtr up = GetParent(target);
                for (int depth = 0; up != IntPtr.Zero && depth < 3 && !found; depth++)
                {
                    found = _rules.TryGetValue(Cls(up), out want);
                    if (!found) up = GetParent(up);
                }
            }
        }
        }
        if (!found) return;                              // 没规则、也不在绘图区：一律不动

        if (target == _lastTarget && want == _lastWant) return;  // 只在窗口/规则变化时动手，不每帧抢
        if (target == _userOverride && target == _lastTarget) return;  // 用户刚手动切过这个窗口，别翻回去
        if ((DateTime.Now - _lastAct).TotalMilliseconds < 120) return;

        _userOverride = IntPtr.Zero;

        int before, after; string channel;
        bool ok = Switch(target, want == 1, out before, out after, out channel);
        _lastTarget = target; _lastWant = want; _lastAct = DateTime.Now; _lastChannel = channel;
        string why = hitPrefix == "LSP" ? ("LSP(" + _lspLine + ")")
                     : (inDraw ? "绘图区(MDIClient)→英文"
                     : (hitPrefix != null ? "前缀(" + hitPrefix + ")" : "类名表"));
        SetTrayState(want == 1, channel);   // v6.5：托盘图标跟着 CAD 的中/英状态变
        if (channel == "shift")
            Log(string.Format("cls={0} 判据={1} 目标={2} IME={3} 通道=shift 已发shift={4}（IMM32 读数不可信 conv 0x{5:X4}）| 父链={6}",
                cls, why, want == 1 ? "中" : "英", _lastIme.Length > 0 ? _lastIme : "未识别", ok ? "是" : "否", Math.Max(before, 0), chain));
        else if (channel == "imm32open")
            Log(string.Format("cls={0} 判据={1} 目标={2} IME={3} 通道=imm32open 开 {4}->{5} {6} | 父链={7}",
                cls, why, want == 1 ? "中" : "英", _lastIme.Length > 0 ? _lastIme : "未识别",
                before > 0 ? "开" : "关", after > 0 ? "开" : "关", ok ? "OK" : "未生效", chain));
        else if (before >= 0)
            Log(string.Format("cls={0} 判据={1} 目标={2} IME={3} 通道={4} conv 0x{5:X4}->0x{6:X4} {7} | 父链={8}",
                cls, why, want == 1 ? "中" : "英", _lastIme.Length > 0 ? _lastIme : "未识别",
                channel, before, Math.Max(after, 0), ok ? "OK" : "未生效", chain));
        else
            Log(string.Format("cls={0} 判据={1} 目标={2} IME={3} 通道={4} 拿不到 IME 窗口(0)，本窗口不动 | 父链={5}",
                cls, why, want == 1 ? "中" : "英", _lastIme.Length > 0 ? _lastIme : "未识别", channel, chain));
    }

    /// <summary>
    /// 类名前缀规则：焦点控件**或它的父链**里有类名以某前缀开头的窗口，就用该规则。
    /// CAD 2024 的命令行是 WPF 窗口（HwndWrapper[DefaultDomain;;&lt;GUID&gt;]），
    /// GUID 每次启动都变，只能按前缀认；走父链是因为焦点可能落在它的子窗口上。
    /// </summary>
    static bool PrefixWant(IntPtr h, out int want, out string hit)
    {
        want = -1; hit = null;
        if (_prefixRules.Count == 0) return false;
        IntPtr p = h;
        for (int depth = 0; depth < 7 && p != IntPtr.Zero; depth++)
        {
            string c = Cls(p);
            for (int i = 0; i < _prefixRules.Count; i++)
            {
                if (c.StartsWith(_prefixRules[i].Key, StringComparison.OrdinalIgnoreCase))
                {
                    want = _prefixRules[i].Value; hit = _prefixRules[i].Key;
                    return true;
                }
            }
            p = GetParent(p);
        }
        return false;
    }

    /// <summary>
    /// 父链里是否含 MDIClient（AutoCAD 绘图区）。顺带把父链拼出来给日志看。
    /// 只认 MDIClient 这个**系统窗口类**，不依赖会变的 Afx: 动态类名。
    /// </summary>
    static bool InDrawArea(IntPtr h, out string chain)
    {
        chain = Cls(h);
        bool hasMdi = chain.IndexOf("MDIClient", StringComparison.OrdinalIgnoreCase) >= 0;
        bool hasCef = IsCef(chain);
        IntPtr p = h;
        for (int i = 0; i < 10; i++)
        {
            p = GetParent(p);
            if (p == IntPtr.Zero) break;
            string c = Cls(p);
            chain += " > " + c;
            if (c.IndexOf("MDIClient", StringComparison.OrdinalIgnoreCase) >= 0) hasMdi = true;
            if (IsCef(c)) hasCef = true;
        }
        // v5 收紧：MDI 区里嵌的 Chromium/CEF/WebView2 窗口不算"绘图区"。
        // 实测证据（2026-10-24 10:48:37）：
        //   cls=Chrome_WidgetWin_1 父链 … > Afx:…:3:… > MDIClient 被当成绘图区强切中文，
        //   而且同一秒里进出反复触发，日志抖动。排除后这些窗口回落到前缀/类名表，没规则就不动作。
        return hasMdi && !hasCef;
    }

    /// <summary>类名是不是 Chromium 系（CEF / WebView2 / Electron）。</summary>
    static bool IsCef(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0
            || cls.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0
            || cls.IndexOf("CefBrowserWindow", StringComparison.OrdinalIgnoreCase) >= 0
            || cls.IndexOf("msedgewebview2", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 自测：自己拉起一个 host.exe（普通 Win32 编辑框），把前台给它，
    /// 用**和正式运行完全相同的 SetChinese()** 去改它的中/英，然后回读。
    /// 本程序是 winexe，结果写 selftest.txt。
    /// </summary>
    static void SelfTest()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        string trace = Path.Combine(_dir, "st.trace.txt");
        Action<string> T = delegate(string s)
        {
            try { File.AppendAllText(trace, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n", Encoding.UTF8); } catch { }
        };
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest.txt");
        try
        {
            T("enter");
            string hostExe = Path.Combine(_dir, "host.exe");
            w("== CadImeLite selftest " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (!File.Exists(hostExe)) { w("host.exe 不存在: " + hostExe); return; }

            var psi = new System.Diagnostics.ProcessStartInfo(hostExe, "hostauto");
            psi.UseShellExecute = true;          // 分离，不继承标准输出管道
            System.Diagnostics.Process.Start(psi);
            T("host started");

            IntPtr host = IntPtr.Zero;
            for (int i = 0; i < 40 && host == IntPtr.Zero; i++)
            {
                System.Threading.Thread.Sleep(150);
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    var t = new StringBuilder(200); GetWindowText(h, t, t.Capacity);
                    if (t.ToString() == "IME2HOST") { host = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            T("host window found=" + (host != IntPtr.Zero));
            if (host == IntPtr.Zero) { w("找不到 IME2HOST 窗口"); return; }

            w("host 窗口=0x" + host.ToInt64().ToString("X"));
            SetForegroundWindow(host);
            System.Threading.Thread.Sleep(500);
            IntPtr fg = GetForegroundWindow();
            w("前台=0x" + fg.ToInt64().ToString("X") + (fg == host ? " (=host)" : " (不是 host)"));
            T("foreground done");

            IntPtr target = FocusOf(host);
            w("真实焦点控件 cls=" + Cls(target) + " hwnd=0x" + target.ToInt64().ToString("X"));
            w("ImmGetDefaultIMEWnd=0x" + ImmGetDefaultIMEWnd(target).ToInt64().ToString("X"));
            w("初始 conv=0x" + GetConv(target).ToString("X4") + " open=" + GetOpen(target));
            T("state read");

            int b, a;
            bool ok1 = SetChinese(target, false, out b, out a);
            w(string.Format("→英文 ok={0} conv 0x{1:X4}->0x{2:X4} native={3}", ok1, b, Math.Max(a, 0), (a & IME_CMODE_NATIVE) != 0));
            bool ok2 = SetChinese(target, true, out b, out a);
            w(string.Format("→中文 ok={0} conv 0x{1:X4}->0x{2:X4} native={3}", ok2, b, Math.Max(a, 0), (a & IME_CMODE_NATIVE) != 0));
            bool ok3 = SetChinese(target, false, out b, out a);
            w(string.Format("→英文(again) ok={0} conv 0x{1:X4}->0x{2:X4}", ok3, b, Math.Max(a, 0)));
            T("switch done");

            PostMessage(host, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE，不用强杀
            w("已请求 host 关闭");
        }
        catch (Exception ex) { w("异常: " + ex); T("exception " + ex.Message); }
        finally
        {
            try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
            T("written");
        }
    }

    /// <summary>
    /// 端到端自测：走**正式运行的 Tick()**（进程过滤 + 焦点控件 + 类名规则 + 切换 + 写日志），
    /// 目标是 host.exe。验证「规则→切换」整条链，而不只是 SetChinese。
    /// </summary>
    static void SelfTest2()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest2.log");
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest2.txt");
        try
        {
            string hostExe = Path.Combine(_dir, "host.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(hostExe, "hostauto");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr host = IntPtr.Zero;
            for (int i = 0; i < 40 && host == IntPtr.Zero; i++)
            {
                System.Threading.Thread.Sleep(150);
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    var t = new StringBuilder(200); GetWindowText(h, t, t.Capacity);
                    if (t.ToString() == "IME2HOST") { host = h; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            if (host == IntPtr.Zero) { w("找不到 IME2HOST"); return; }
            SetForegroundWindow(host);
            System.Threading.Thread.Sleep(400);

            IntPtr target = FocusOf(host);
            string cls = Cls(target);
            w("host=0x" + host.ToInt64().ToString("X") + " focus cls=" + cls);

            _scope.Add("host");                 // 让 Tick 认为 host.exe 在作用范围内

            _rules[cls] = 0;                    // 规则：这个窗口要英文
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            Tick();
            w("Tick#1(规则=英文) 后 conv=0x" + GetConv(target).ToString("X4") + " native=" + ((GetConv(target) & 1) != 0));

            _rules[cls] = 1;                    // 规则改成中文，Tick 应重新应用
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            Tick();
            w("Tick#2(规则=中文) 后 conv=0x" + GetConv(target).ToString("X4") + " native=" + ((GetConv(target) & 1) != 0));

            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            _scope.Clear(); _scope.Add("acad"); // 前台不是 acad -> Tick 必须什么都不做
            _rules[cls] = 0;
            long convBefore = GetConv(target);
            Tick();
            w("Tick#3(scope=acad，前台是 host) 后 conv=0x" + GetConv(target).ToString("X4") +
              " 应保持不变(" + (GetConv(target) == convBefore) + ")");

            PostMessage(host, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    /// <summary>
    /// 端到端自测 3：验证「父链含 MDIClient → 中文」且优先级高于类名表。
    /// host3.exe 造两棵树：
    ///   IME2HOST  Form -> MDIClient(系统类) -> TextBox   （模拟绘图区/多行文字在位编辑器）
    ///   IME2HOST2 Form -> TextBox                        （模拟命令行）
    /// 两棵树的焦点控件类名相同，rules 里都配成 0(英文)：
    ///   有 MDIClient 的必须变中文，没有的必须变英文 —— 这就是判据是否生效的证明。
    /// </summary>
    static void SelfTest3()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest3.log");
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest3.txt");
        try
        {
            string exe = Path.Combine(_dir, "host3.exe");
            if (!File.Exists(exe)) { w("host3.exe 不存在"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "hostauto");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr h1 = FindByTitle("IME2HOST");
            IntPtr h2 = FindByTitle("IME2HOST2");
            for (int i = 0; i < 30 && (h1 == IntPtr.Zero || h2 == IntPtr.Zero); i++)
            {
                System.Threading.Thread.Sleep(150);
                if (h1 == IntPtr.Zero) h1 = FindByTitle("IME2HOST");
                if (h2 == IntPtr.Zero) h2 = FindByTitle("IME2HOST2");
            }
            w("== CadImeLite selftest3 " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (h1 == IntPtr.Zero || h2 == IntPtr.Zero) { w("没找到测试窗口 h1=" + h1 + " h2=" + h2); return; }

            _scope.Add("host3");

            ForceForeground(h1);
            System.Threading.Thread.Sleep(500);
            IntPtr t1 = FocusOf(h1);
            string c1 = Cls(t1);
            w("A) 绘图区里的编辑框: cls=" + c1);
            string chain1;
            bool inDraw = InDrawArea(t1, out chain1);
            w("   父链=" + chain1);
            w("   InDrawArea=" + inDraw + "（期望 True）");

            _rules[c1] = 1;                       // 类名表故意配中文，看绘图区判据(英文)能不能压过它
            int b0, a0;
            SetChinese(t1, true, out b0, out a0);    // 先手动压成中文，制造"要切"的场景
            System.Threading.Thread.Sleep(200);
            w("   先压成中文后 conv=0x" + GetConv(t1).ToString("X4"));
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            w("   [调试] 前台=0x" + GetForegroundWindow().ToInt64().ToString("X") + " proc=" + ProcName(GetForegroundWindow()) + " scope=" + string.Join(",", _scope.ToArray()));
            Tick();
            int conv1 = GetConv(t1);
            w("   Tick 后 conv=0x" + conv1.ToString("X4") + " native=" + ((conv1 & 1) != 0) + "（期望 False=英文，绘图区判据覆盖类名表）");

            ForceForeground(h2);
            System.Threading.Thread.Sleep(500);
            IntPtr t2 = FocusOf(h2);
            string c2 = Cls(t2);
            w("B) 普通编辑框(命令行模拟): cls=" + c2);
            string chain2;
            bool inDraw2 = InDrawArea(t2, out chain2);
            w("   父链=" + chain2);
            w("   InDrawArea=" + inDraw2 + "（期望 False）");

            _rules[c2] = 1;                        // 同样的类名规则=中文
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            Tick();
            int conv2 = GetConv(t2);
            w("   Tick 后 conv=0x" + conv2.ToString("X4") + " native=" + ((conv2 & 1) != 0) + "（期望 True=中文，按类名表）");

            w("");
            w("结论: A " + (((conv1 & 1) == 0) && inDraw ? "通过" : "失败") +
              " / B " + ((((conv2 & 1) != 0) && !inDraw2) ? "通过" : "失败"));
            PostMessage(h1, 0x0010, IntPtr.Zero, IntPtr.Zero);
            PostMessage(h2, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    /// <summary>自测用：把目标窗口强行提到前台（前台锁会让 SetForegroundWindow 静默失败）。</summary>
    static void ForceForeground(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == h) return;
        uint pid;
        uint fgtid = GetWindowThreadProcessId(fg, out pid);
        uint me = GetCurrentThreadId();
        AttachThreadInput(me, fgtid, true);
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(250);
        AttachThreadInput(me, fgtid, false);
    }

    static IntPtr FindByClassPrefix(string prefix)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            if (Cls(h).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr FindByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            var t = new StringBuilder(200); GetWindowText(h, t, t.Capacity);
            if (t.ToString() == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// 端到端自测 4：验证「焦点离开 scope 时把 CAD 控件复位成中文」。
    /// 步骤：在 MDI 树里切成英文 → 把 scope 换掉（等价于前台不再是 acad）→ Tick 应复位成中文，
    /// 且只复位一次（第二次 Tick 不再写日志）。
    /// </summary>
    static void SelfTest4()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest4.log");
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest4.txt");
        try
        {
            string exe = Path.Combine(_dir, "host3.exe");
            if (!File.Exists(exe)) { w("host3.exe 不存在"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "plainfirst");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr h1 = IntPtr.Zero;
            // 用"命令行那一棵树"（普通窗体 + EDIT，父链无 MDIClient），才会被判成英文
            for (int i = 0; i < 40 && h1 == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(150); h1 = FindByTitle("IME2HOST2"); }
            w("== CadImeLite selftest4 " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (h1 == IntPtr.Zero) { w("没找到 IME2HOST2"); return; }

            _scope.Add("host3");
            ForceForeground(h1);
            System.Threading.Thread.Sleep(500);
            w("前台=0x" + GetForegroundWindow().ToInt64().ToString("X") + (GetForegroundWindow() == h1 ? " (=IME2HOST2)" : " (不是 IME2HOST2)"));
            IntPtr t = FocusOf(h1);
            string cls = Cls(t);
            w("焦点控件 cls=" + cls);

            _rules[cls] = 0;                       // 规则=英文，模拟"在 CAD 命令行/编辑区被切成英文"
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            // 先把目标压成英文（等价于工具之前在 CAD 里把它切成了英文）
            int sb0, sa0;
            SetChinese(t, false, out sb0, out sa0);
            System.Threading.Thread.Sleep(150);
            _lastTarget = t; _lastWant = 0; _lastAct = DateTime.Now;   // 模拟工具"刚在 CAD 里设过英文"
            int c1 = GetConv(t);
            w("① 准备态（CAD 里=英文）conv=0x" + c1.ToString("X4") + " native=" + ((c1 & 1) != 0) + "（期望 False=英文）");

            // 等价于"前台已经切到 scope 外的应用"：把 scope 换成别的进程名，前台随便是什么都算离开
            _scope.Clear(); _scope.Add("nosuchprocess");
            Tick();
            int c2 = GetConv(t);
            w("② 离开 acad 后 conv=0x" + c2.ToString("X4") + " native=" + ((c2 & 1) != 0) + "（期望 True=中文）");

            string[] logs = File.Exists(_logPath) ? File.ReadAllLines(_logPath, Encoding.UTF8) : new string[0];
            int lines2 = logs.Length;
            string last = lines2 > 0 ? logs[lines2 - 1] : "";
            w("③ 复位日志: " + last);

            Tick();                                 // 再 Tick 一次，不应再复位（记账已清）
            int lines3 = File.Exists(_logPath) ? File.ReadAllLines(_logPath, Encoding.UTF8).Length : 0;
            w("④ 再 Tick 一次后日志行数 " + lines2 + " -> " + lines3 + "（期望不变，只复位一次）");

            w("");
            w("结论: 复位=" + (((c2 & 1) != 0) ? "通过" : "失败") +
              " / 只复位一次=" + ((lines3 == lines2) ? "通过" : "失败") +
              " / 离开日志=" + (last.IndexOf("离开acad") >= 0 ? "通过" : "失败"));
            PostMessage(h1, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    /// <summary>
    /// 端到端自测 5：验证「类名前缀 HwndWrapper[DefaultDomain → 英文」。
    /// host4.exe 造的就是一个 WPF 风格窗口（类名带 GUID），等价于 CAD 2024 的命令行窗口。
    /// </summary>
    static void SelfTest5()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest5.log");
        LoadRules();
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest5.txt");
        try
        {
            string exe = Path.Combine(_dir, "host4.exe");
            if (!File.Exists(exe)) { w("host4.exe 不存在"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(exe);
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr h1 = IntPtr.Zero;
            for (int i = 0; i < 40 && h1 == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(150); h1 = FindByClassPrefix("HwndWrapper[DefaultDomain"); }
            w("== CadImeLite selftest5 " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (h1 == IntPtr.Zero) { w("没找到 HwndWrapper[DefaultDomain 窗口"); return; }

            _scope.Add("host4");
            ForceForeground(h1);
            IntPtr t = FocusOf(h1);
            string cls = Cls(t);
            w("焦点控件 cls=" + cls);
            w("前缀规则数=" + _prefixRules.Count + "（内置 HwndWrapper[DefaultDomain）");

            // 先压成中文，再看 Tick 能不能按前缀切成英文
            int b0, a0;
            SetChinese(t, true, out b0, out a0);
            System.Threading.Thread.Sleep(150);
            w("准备态 conv=0x" + GetConv(t).ToString("X4") + "（期望中文 0x0001）");

            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            Tick();
            int c1 = GetConv(t);
            w("Tick 后 conv=0x" + c1.ToString("X4") + " native=" + ((c1 & 1) != 0) + "（期望 False=英文）");

            string[] logs = File.Exists(_logPath) ? File.ReadAllLines(_logPath, Encoding.UTF8) : new string[0];
            string last = logs.Length > 0 ? logs[logs.Length - 1] : "";
            w("日志: " + last);
            w("");
            w("结论: 前缀判据=" + (last.IndexOf("判据=前缀") >= 0 ? "通过" : "失败") +
              " / 切成英文=" + (((c1 & 1) == 0) ? "通过" : "失败"));
            PostMessage(h1, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    /// <summary>
    /// 端到端自测 6（v5）：MDI 区里嵌的 Chromium 窗口不应再被判成"绘图区"。
    /// host5.exe 造 MDI 主窗体 -> 真 MDIClient -> MDI 子窗体 -> Chrome_WidgetWin_1。
    /// 断言：InDrawArea=False；类名表配中文时按表走（判据=类名表），不再被强切英文。
    /// </summary>
    static void SelfTest6()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest6.log");
        LoadRules();
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest6.txt");
        try
        {
            string exe = Path.Combine(_dir, "host5.exe");
            if (!File.Exists(exe)) { w("host5.exe 不存在"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "auto");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr h1 = IntPtr.Zero;
            for (int i = 0; i < 40 && h1 == IntPtr.Zero; i++)
            {
                System.Threading.Thread.Sleep(150);
                h1 = FindByClassPrefix("Chrome_WidgetWin");
            }
            w("== CadImeLite selftest6 " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (h1 == IntPtr.Zero) { w("没找到 Chrome_WidgetWin 窗口"); return; }

            _scope.Add("host5");
            ForceForeground(FindByTitle("IME3HOST"));
            System.Threading.Thread.Sleep(400);
            IntPtr t = FocusOf(h1);
            string cls = Cls(t);
            string chain;
            bool inDraw = InDrawArea(t, out chain);
            w("焦点控件 cls=" + cls);
            w("父链=" + chain);
            w("InDrawArea=" + inDraw + "（期望 False，v5 之前是 True）");

            _rules[cls] = 1;                         // 类名表配中文
            int b0, a0;
            SetChinese(t, false, out b0, out a0);    // 先压成英文
            System.Threading.Thread.Sleep(150);
            _lastTarget = IntPtr.Zero; _lastWant = -1; _lastAct = DateTime.MinValue;
            Tick();
            int c1 = GetConv(t);
            string[] logs = File.Exists(_logPath) ? File.ReadAllLines(_logPath, Encoding.UTF8) : new string[0];
            string last = logs.Length > 0 ? logs[logs.Length - 1] : "";
            w("Tick 后 conv=0x" + c1.ToString("X4") + " native=" + ((c1 & 1) != 0));
            w("日志: " + last);

            w("");
            w("结论: 不被当绘图区=" + (!inDraw ? "通过" : "失败") +
              " / 走类名表(不按绘图区)=" + ((last.IndexOf("判据=类名表") >= 0 && last.IndexOf("判据=绘图区") < 0) ? "通过" : "失败"));
            IntPtr hm = FindByTitle("IME3HOST");
            if (hm != IntPtr.Zero) PostMessage(hm, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    /// <summary>
    /// 端到端自测 7（v5）：离开复位去抖。
    /// 场景：CAD 里被设成英文 → 离开 → 复位一次；马上再离开 → 1 秒内不重复；
    /// 等 1.2 秒后再离开 → 再复位；工具没设过英文（_lastWant=1）→ 不复位；
    /// 已经是中文 → 不复位也不写日志。
    /// </summary>
    static void SelfTest7()
    {
        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "selftest7.log");
        LoadRules();
        var sb = new StringBuilder();
        Action<string> w = delegate(string s) { sb.AppendLine(s); };
        string outPath = Path.Combine(_dir, "selftest7.txt");
        try
        {
            string exe = Path.Combine(_dir, "host3.exe");
            if (!File.Exists(exe)) { w("host3.exe 不存在"); return; }
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "plainfirst");
            psi.UseShellExecute = true;
            System.Diagnostics.Process.Start(psi);

            IntPtr h2 = IntPtr.Zero;
            for (int i = 0; i < 40 && h2 == IntPtr.Zero; i++) { System.Threading.Thread.Sleep(150); h2 = FindByTitle("IME2HOST2"); }
            w("== CadImeLite selftest7 " + DateTime.Now.ToString("HH:mm:ss") + " ==");
            if (h2 == IntPtr.Zero) { w("没找到 IME2HOST2"); return; }

            _scope.Add("nosuchprocess");           // 前台永远"不在 scope 内"= 每次 Tick 都走离开分支
            ForceForeground(h2);
            IntPtr t = FocusOf(h2);
            w("焦点控件 cls=" + Cls(t));

            Func<int> logLines = delegate { return File.Exists(_logPath) ? File.ReadAllLines(_logPath, Encoding.UTF8).Length : 0; };
            int n0 = logLines();

            // ① 英文 + 工具设过英文 → 应复位
            int b, a; SetChinese(t, false, out b, out a);
            _lastTarget = t; _lastWant = 0;
            Tick();
            int c1 = GetConv(t);
            int n1 = logLines();
            w("① 首次离开: conv=0x" + c1.ToString("X4") + " 日志 +" + (n1 - n0) + "（期望 +1，且变中文）");

            // ② 立刻再离开（1 秒内）→ 去抖，不应重复
            SetChinese(t, false, out b, out a);
            _lastTarget = t; _lastWant = 0;
            Tick();
            int n2 = logLines();
            w("② 1 秒内再离开: 日志 +" + (n2 - n1) + "（期望 0，去抖）");

            // ③ 等 1.2 秒后再离开 → 允许复位
            System.Threading.Thread.Sleep(1200);
            SetChinese(t, false, out b, out a);
            _lastTarget = t; _lastWant = 0;
            Tick();
            int n3 = logLines();
            w("③ 1.2 秒后再离开: 日志 +" + (n3 - n2) + "（期望 +1）");

            // ④ 工具最后一次设的是中文（用户自己按 Shift 改成英文）→ 不复位
            System.Threading.Thread.Sleep(1200);
            SetChinese(t, false, out b, out a);
            _lastTarget = t; _lastWant = 1;
            Tick();
            int c4 = GetConv(t);
            int n4 = logLines();
            w("④ 用户自己改成英文后离开: 日志 +" + (n4 - n3) + "（期望 0，不复位），conv=0x" + c4.ToString("X4"));

            // ⑤ 已经是中文 → 不复位也不写日志
            System.Threading.Thread.Sleep(1200);
            SetChinese(t, true, out b, out a);
            _lastTarget = t; _lastWant = 0;
            Tick();
            int n5 = logLines();
            w("⑤ 已经是中文时离开: 日志 +" + (n5 - n4) + "（期望 0，不写 0x0001->0x0001 噪声）");

            w("");
            w("结论: 首次复位=" + ((n1 - n0 == 1 && (c1 & 1) != 0) ? "通过" : "失败") +
              " / 去抖=" + ((n2 - n1 == 0) ? "通过" : "失败") +
              " / 过期后可复位=" + ((n3 - n2 == 1) ? "通过" : "失败") +
              " / 用户改的不复位=" + ((n4 - n3 == 0) ? "通过" : "失败") +
              " / 中文不重复复位=" + ((n5 - n4 == 0) ? "通过" : "失败"));
            PostMessage(h2, 0x0010, IntPtr.Zero, IntPtr.Zero);
            IntPtr hm = FindByTitle("IME2HOST");
            if (hm != IntPtr.Zero) PostMessage(hm, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { w("异常: " + ex.Message); }
        finally { try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { } }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args != null && args.Length > 0 && args[0] == "selftest") { SelfTest(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest2") { SelfTest2(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest3") { SelfTest3(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest4") { SelfTest4(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest5") { SelfTest5(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest6") { SelfTest6(); return; }
        if (args != null && args.Length > 0 && args[0] == "selftest7") { SelfTest7(); return; }

        _dir = AppDomain.CurrentDomain.BaseDirectory;
        _logPath = Path.Combine(_dir, "ime-lite.log");
        if (!EnsureSingleInstance()) return;      // v6.4：拿不到互斥体就静默退出
        Application.ApplicationExit += delegate
        {
            try { if (_singleMutex != null) { _singleMutex.ReleaseMutex(); _singleMutex.Dispose(); _singleMutex = null; } } catch { }
        };
        LoadRules();
        InstallShiftHook();

        var icon = new NotifyIcon();
        _tray = icon;
        SetTrayState(false, "启动");      // v6.5：托盘先画「英」，之后随 CAD 状态切换
        if (icon.Icon == null) icon.Icon = SystemIcons.Application;
        icon.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开日志", null, delegate { try { System.Diagnostics.Process.Start("notepad.exe", _logPath); } catch { } });
        var miPause = new ToolStripMenuItem("暂停");
        miPause.Click += delegate
        {
            _paused = !_paused;
            miPause.Text = _paused ? "继续" : "暂停";
            _lastTarget = IntPtr.Zero;
            Log(_paused ? "已暂停" : "已继续");
        };
        menu.Items.Add(miPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { icon.Visible = false; Application.Exit(); });
        icon.ContextMenuStrip = menu;

        var timer = new Timer();
        timer.Interval = 200;
        timer.Tick += delegate { try { Tick(); } catch (Exception ex) { Log("异常: " + ex.Message); } };
        timer.Start();
        Log("启动 v6.5 scope=" + string.Join(",", _scope.ToArray()) + " rules=" + _rules.Count + " 通道=" + _channel);
        Application.Run();
    }
}





