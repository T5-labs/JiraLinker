using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JiraLinker;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single-instance guard so login auto-start + manual launch don't double up.
        using var mutex = new Mutex(true, "JiraLinker_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}

/// <summary>Tray icon + lifetime owner for the keyboard hook.</summary>
sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly KeyHook _hook;

    public TrayContext()
    {
        _hook = new KeyHook();
        _hook.Install();

        var menu = new ContextMenuStrip();

        var toggle = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) =>
        {
            _hook.Enabled = toggle.Checked;
            _icon.Text = toggle.Checked
                ? "Jira Linker — on (CMMS-#### , MCP-####)"
                : "Jira Linker — paused";
        };
        menu.Items.Add(toggle);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Type CMMS-#### or MCP-#### then space") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = true,
            Text = "Jira Linker — on (CMMS-#### , MCP-####)",
            ContextMenuStrip = menu
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hook.Dispose();
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Global low-level keyboard hook. Buffers typed characters and, when a
/// word-boundary key is pressed, rewrites a trailing CMMS-#### / MCP-#### token
/// into its full Jira browse URL.
/// </summary>
sealed class KeyHook : IDisposable
{
    private const string BaseUrl = "https://herzog.atlassian.net/browse/";

    // Ticket token at the end of the buffer, not glued to a preceding alphanumeric.
    private static readonly Regex Pattern =
        new(@"(?<![A-Za-z0-9])(CMMS|MCP)-(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly LowLevelKeyboardProc _proc; // kept alive to avoid GC of the callback
    private IntPtr _hookId = IntPtr.Zero;
    private readonly StringBuilder _buf = new();
    private readonly StringBuilder _charBuf = new(8); // scratch for ToUnicodeEx
    private bool _lShift, _rShift, _ctrl, _alt, _win;

    private bool Shift => _lShift || _rShift;

    private readonly System.Windows.Forms.Timer _restoreTimer;
    private DataObject? _savedClipboard;
    private bool _restorePending;

    public bool Enabled { get; set; } = true;

    public KeyHook()
    {
        _proc = HookCallback;
        _restoreTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _restoreTimer.Tick += (_, _) => { _restoreTimer.Stop(); RestoreClipboard(); };
    }

    public void Install()
    {
        using var curProc = Process.GetCurrentProcess();
        using var curMod = curProc.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curMod.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install keyboard hook (error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        int vk = (int)data.vkCode;
        bool injected = (data.flags & LLKHF_INJECTED) != 0;
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

        // Track modifier state (physical), always pass through.
        if (vk == 0xA1) { if (isDown) _rShift = true; else if (isUp) _rShift = false; return CallNextHookEx(_hookId, nCode, wParam, lParam); }
        if (vk == 0x10 || vk == 0xA0) { if (isDown) _lShift = true; else if (isUp) _lShift = false; return CallNextHookEx(_hookId, nCode, wParam, lParam); }
        if (IsCtrl(vk))  { if (isDown) _ctrl  = true; else if (isUp) _ctrl  = false; return CallNextHookEx(_hookId, nCode, wParam, lParam); }
        if (IsAlt(vk))   { if (isDown) _alt   = true; else if (isUp) _alt   = false; return CallNextHookEx(_hookId, nCode, wParam, lParam); }
        if (IsWin(vk))   { if (isDown) _win   = true; else if (isUp) _win   = false; return CallNextHookEx(_hookId, nCode, wParam, lParam); }

        // Only react to physical key-down events while enabled.
        if (!isDown || injected || !Enabled)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // A modifier combo is a shortcut, not text — reset and ignore.
        if (_ctrl || _alt || _win)
        {
            _buf.Clear();
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Backspace edits the buffer to mirror the field.
        if (vk == VK_BACK)
        {
            if (_buf.Length > 0) _buf.Length--;
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Characters that are part of a ticket token (letters, digits, hyphen).
        char? bufferChar = BufferChar(vk, Shift);
        if (bufferChar.HasValue)
        {
            _buf.Append(bufferChar.Value);
            if (_buf.Length > 48) _buf.Remove(0, _buf.Length - 48);
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Enter / Tab end the word and are re-emitted as virtual keys.
        // Any other key that produces a printable character (space and every
        // punctuation/symbol) is also a trigger and is re-emitted as that character.
        bool isEnter = vk == VK_RETURN;
        bool isTab = vk == VK_TAB;
        char triggerChar = '\0';
        bool isPrintable = !isEnter && !isTab && TryGetPrintableChar((uint)vk, data.scanCode, out triggerChar);

        if (isEnter || isTab || isPrintable)
        {
            var m = Pattern.Match(_buf.ToString());
            if (m.Success)
            {
                int tokenLen = m.Value.Length;
                string display = m.Groups[1].Value.ToUpperInvariant() + "-" + m.Groups[2].Value;
                string url = BaseUrl + display;
                _buf.Clear();
                ReplaceWithLink(tokenLen, url, display, vk, isPrintable ? triggerChar : null);
                return (IntPtr)1; // swallow the original key; we re-emit it after pasting the link
            }
            _buf.Clear();
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Anything else (arrows, Esc, function keys, etc.) just ends the word.
        _buf.Clear();
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>Maps a virtual key to a token character, or null if it isn't one.</summary>
    private static char? BufferChar(int vk, bool shift)
    {
        if (vk >= 0x41 && vk <= 0x5A) return (char)vk;                  // A-Z
        if (vk >= 0x30 && vk <= 0x39) return shift ? null : (char)vk;   // 0-9 (Shift makes a symbol)
        if (vk >= 0x60 && vk <= 0x69) return (char)('0' + (vk - 0x60)); // numpad 0-9
        if (vk == VK_OEM_MINUS) return shift ? null : '-';
        if (vk == VK_SUBTRACT) return '-';                              // numpad minus
        return null;
    }

    /// <summary>
    /// Resolves the printable character a key would produce given the current Shift
    /// state, honoring the active keyboard layout. Returns false for keys that don't
    /// produce a simple printable char (Enter, Tab, arrows, function keys, dead keys…).
    /// </summary>
    private bool TryGetPrintableChar(uint vk, uint scanCode, out char c)
    {
        c = '\0';
        var keyState = new byte[256];
        if (Shift) { keyState[0x10] = 0x80; keyState[0xA0] = 0x80; }

        // Layout of the window actually receiving input.
        IntPtr hkl = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));

        _charBuf.Clear();
        // wFlags bit 2 (0x4): don't mutate the kernel keyboard state (avoids breaking dead keys).
        int rc = ToUnicodeEx(vk, scanCode, keyState, _charBuf, _charBuf.Capacity, 0x4, hkl);
        if (rc == 1)
        {
            char ch = _charBuf[0];
            if (ch >= 0x20 && ch != 0x7F) { c = ch; return true; } // printable, excludes control chars/DEL
        }
        return false;
    }

    /// <summary>
    /// Deletes the typed token and pastes a named hyperlink (HTML + RTF) so the
    /// field shows e.g. "CMMS-2747" but links to the full URL. Falls back to
    /// typing the raw URL if the clipboard can't be used.
    /// </summary>
    private void ReplaceWithLink(int backspaces, string url, string display, int vk, char? unicodeTrigger)
    {
        DataObject payload;
        try
        {
            string fragment = $"<a href=\"{HtmlEscape(url)}\">{HtmlEscape(display)}</a>";
            string cfHtml = BuildCfHtml(fragment);
            string rtf = @"{\rtf1\ansi{\field{\*\fldinst{HYPERLINK """ + url + @"""}}{\fldrslt{\ul " + display + @"}}}}";

            payload = new DataObject();
            payload.SetData(DataFormats.Html, new MemoryStream(Encoding.UTF8.GetBytes(cfHtml)));
            payload.SetData(DataFormats.Rtf, rtf);
            payload.SetData(DataFormats.UnicodeText, url); // plain-text fields still get a working link
        }
        catch
        {
            SendReplacement(backspaces, url, vk, unicodeTrigger);
            return;
        }

        // Preserve the user's existing clipboard; restore it shortly after the paste.
        if (!_restorePending)
            _savedClipboard = SnapshotClipboard();
        _restorePending = true;

        try { Clipboard.SetDataObject(payload, true); }
        catch { _restorePending = false; SendReplacement(backspaces, url, vk, unicodeTrigger); return; }

        // If Shift is physically held (e.g. the trigger was a shifted symbol like ")"),
        // release it so the synthetic Ctrl+V doesn't become Ctrl+Shift+V (paste-as-text
        // in many apps). We restore it afterwards so the OS state matches the real key.
        bool lShift = _lShift, rShift = _rShift;

        var inputs = new List<INPUT>(backspaces * 2 + 10);
        if (lShift) inputs.Add(KeyVk(VK_LSHIFT, true));
        if (rShift) inputs.Add(KeyVk(VK_RSHIFT, true));

        for (int i = 0; i < backspaces; i++)
        {
            inputs.Add(KeyVk(VK_BACK, false));
            inputs.Add(KeyVk(VK_BACK, true));
        }
        // Ctrl+V (with Shift released)
        inputs.Add(KeyVk(VK_CONTROL, false));
        inputs.Add(KeyVk(VK_V, false));
        inputs.Add(KeyVk(VK_V, true));
        inputs.Add(KeyVk(VK_CONTROL, true));

        // Restore the physical Shift state before re-emitting, so Shift+Enter etc. survive.
        if (lShift) inputs.Add(KeyVk(VK_LSHIFT, false));
        if (rShift) inputs.Add(KeyVk(VK_RSHIFT, false));

        // Re-emit the boundary key the user pressed.
        if (vk == VK_RETURN) { inputs.Add(KeyVk(VK_RETURN, false)); inputs.Add(KeyVk(VK_RETURN, true)); }
        else if (vk == VK_TAB) { inputs.Add(KeyVk(VK_TAB, false)); inputs.Add(KeyVk(VK_TAB, true)); }
        else if (unicodeTrigger.HasValue) { inputs.Add(KeyUnicode(unicodeTrigger.Value, false)); inputs.Add(KeyUnicode(unicodeTrigger.Value, true)); }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());

        _restoreTimer.Stop();
        _restoreTimer.Start();
    }

    private static DataObject? SnapshotClipboard()
    {
        try
        {
            var cur = Clipboard.GetDataObject();
            if (cur == null) return null;
            var copy = new DataObject();
            bool any = false;
            foreach (var fmt in cur.GetFormats(false))
            {
                try { var o = cur.GetData(fmt, false); if (o != null) { copy.SetData(fmt, o); any = true; } }
                catch { }
            }
            return any ? copy : null;
        }
        catch { return null; }
    }

    private void RestoreClipboard()
    {
        _restorePending = false;
        try
        {
            if (_savedClipboard != null) Clipboard.SetDataObject(_savedClipboard, true);
            else Clipboard.Clear();
        }
        catch { }
        _savedClipboard = null;
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Wraps an HTML fragment in the CF_HTML descriptor (with UTF-8 byte offsets).</summary>
    private static string BuildCfHtml(string fragment)
    {
        const string headerFmt =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body><!--StartFragment-->";
        const string post = "<!--EndFragment--></body></html>";

        int headerLen = Encoding.UTF8.GetByteCount(string.Format(headerFmt, 0, 0, 0, 0));
        int startFragment = headerLen + Encoding.UTF8.GetByteCount(pre);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(post);

        return string.Format(headerFmt, headerLen, endHtml, startFragment, endFragment) + pre + fragment + post;
    }

    private void SendReplacement(int backspaces, string url, int vk, char? unicodeTrigger)
    {
        var inputs = new List<INPUT>(backspaces * 2 + url.Length * 2 + 2);

        for (int i = 0; i < backspaces; i++)
        {
            inputs.Add(KeyVk(VK_BACK, false));
            inputs.Add(KeyVk(VK_BACK, true));
        }
        foreach (char c in url)
        {
            inputs.Add(KeyUnicode(c, false));
            inputs.Add(KeyUnicode(c, true));
        }

        // Re-emit the boundary key the user actually pressed.
        if (vk == VK_RETURN) { inputs.Add(KeyVk(VK_RETURN, false)); inputs.Add(KeyVk(VK_RETURN, true)); }
        else if (vk == VK_TAB) { inputs.Add(KeyVk(VK_TAB, false)); inputs.Add(KeyVk(VK_TAB, true)); }
        else if (unicodeTrigger.HasValue) { inputs.Add(KeyUnicode(unicodeTrigger.Value, false)); inputs.Add(KeyUnicode(unicodeTrigger.Value, true)); }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyVk(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    private static INPUT KeyUnicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } }
    };

    private static bool IsCtrl(int vk) => vk == 0x11 || vk == 0xA2 || vk == 0xA3;
    private static bool IsAlt(int vk) => vk == 0x12 || vk == 0xA4 || vk == 0xA5;
    private static bool IsWin(int vk) => vk == 0x5B || vk == 0x5C;

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _restoreTimer.Dispose();
    }

    // ---- Win32 interop ----

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private const int VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20;
    private const int VK_OEM_MINUS = 0xBD, VK_OEM_COMMA = 0xBC, VK_OEM_PERIOD = 0xBE, VK_SUBTRACT = 0x6D;
    private const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
}
