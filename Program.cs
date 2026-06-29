using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JiraLinker;

static class Program
{
    // Canonical install location, kept identical to scripts\install.ps1 so the script
    // and the self-installer stay interchangeable.
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JiraLinker");
    public static string InstalledExe => Path.Combine(InstallDir, "JiraLinker.exe");
    public static string StartupShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "JiraLinker.lnk");

    [STAThread]
    static void Main()
    {
        // Must run before any window (incl. a MessageBox) is created, else
        // SetCompatibleTextRenderingDefault throws — so do this before the installer prompt.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Self-install decision happens BEFORE the single-instance mutex: when we install
        // and relaunch, the freshly copied exe needs to claim that same named mutex, so this
        // process must not still be holding it. Returns false => this process should exit.
        if (!MaybeSelfInstall())
            return;

        // Single-instance guard so login auto-start + manual launch don't double up.
        using var mutex = new Mutex(true, "JiraLinker_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        using var ctx = new TrayContext();
        Application.Run(ctx);
    }

    /// <summary>
    /// Offers to install a downloaded copy to the canonical location and auto-start it.
    /// Returns true to continue normal startup in THIS process; false if this process
    /// should exit (installed-and-relaunched, or the user cancelled).
    /// </summary>
    private static bool MaybeSelfInstall()
    {
        // Environment.ProcessPath is correct under PublishSingleFile (ExecutablePath can
        // point at the extracted host); fall back just in case it is ever null.
        string currentExe = Environment.ProcessPath ?? Application.ExecutablePath;

        // Already the installed copy (login autostart or our own post-install relaunch):
        // never prompt, just run.
        if (PathsEqual(currentExe, InstalledExe))
            return true;

        var choice = MessageBox.Show(
            "Install JiraLinker and run it automatically when you sign in?",
            "JiraLinker", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (choice == DialogResult.Cancel)
            return false;        // exit without running
        if (choice == DialogResult.No)
            return true;         // run from the current location for this session

        try
        {
            Directory.CreateDirectory(InstallDir);
            StopOtherInstances(); // release any lock on the target file before overwriting
            File.Copy(currentExe, InstalledExe, overwrite: true);
            CreateStartupShortcut(InstalledExe, InstallDir);
            Process.Start(new ProcessStartInfo { FileName = InstalledExe, WorkingDirectory = InstallDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not install JiraLinker:\n\n" + ex.Message,
                "JiraLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return false; // the installed copy now runs; this one exits
    }

    /// <summary>Kills any OTHER JiraLinker process so the install target isn't file-locked.</summary>
    private static void StopOtherInstances()
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("JiraLinker"))
        {
            try { if (p.Id != me) { p.Kill(); p.WaitForExit(2000); } }
            catch { /* already gone or access denied — best effort */ }
            finally { p.Dispose(); }
        }
    }

    /// <summary>Creates/refreshes the Startup .lnk via late-bound WScript.Shell COM (mirrors install.ps1, no NuGet).</summary>
    private static void CreateStartupShortcut(string targetExe, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("WScript.Shell COM is unavailable.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic sc = shell.CreateShortcut(StartupShortcut);
            sc.TargetPath = targetExe;
            sc.WorkingDirectory = workingDir;
            sc.Description = "Jira Linker - auto-hyperlink Jira ticket keys";
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    /// <summary>Case-insensitive, normalized path comparison (fails closed on bad paths).</summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Tray icon + lifetime owner for the keyboard hook.</summary>
sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly KeyHook _hook;
    private SettingsForm? _settings;

    public TrayContext()
    {
        _hook = new KeyHook();
        _hook.Install();

        var menu = new ContextMenuStrip();

        var toggle = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        toggle.CheckedChanged += (_, _) =>
        {
            _hook.Enabled = toggle.Checked;
            _icon.Text = toggle.Checked ? "Jira Linker - On" : "Jira Linker - Off";
        };
        menu.Items.Add(toggle);

        menu.Items.Add(new ToolStripSeparator());

        var manage = new ToolStripMenuItem("Manage projects…");
        manage.Click += (_, _) => ShowSettings();
        menu.Items.Add(manage);

        menu.Items.Add(new ToolStripSeparator());

        var uninstall = new ToolStripMenuItem("Uninstall");
        uninstall.Click += (_, _) => Uninstall();
        menu.Items.Add(uninstall);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = AppIcons.Tray(),
            Visible = true,
            Text = "Jira Linker - On",
            ContextMenuStrip = menu
        };
        // Open the menu on a single left-click (not just right-click).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowTrayMenu();
        };
    }

    // NotifyIcon only opens its menu on right-click by default; invoke the same
    // internal routine on left-click so a single left-click works identically.
    private static readonly System.Reflection.MethodInfo? ShowContextMenuMethod =
        typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private void ShowTrayMenu()
    {
        if (ShowContextMenuMethod != null)
            ShowContextMenuMethod.Invoke(_icon, null);
        else
            _icon.ContextMenuStrip?.Show(Cursor.Position);
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(_hook.GetProjects(), _hook.GetSuppressChars(), _hook.SetConfig);
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>
    /// Removes the Startup shortcut and the install folder. User config in
    /// %APPDATA%\JiraLinker (projects.json) is intentionally left, matching uninstall.ps1.
    /// </summary>
    private void Uninstall()
    {
        if (MessageBox.Show(
                "Remove JiraLinker? It will stop running and no longer start with Windows.",
                "JiraLinker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        try { if (File.Exists(Program.StartupShortcut)) File.Delete(Program.StartupShortcut); }
        catch { /* best effort — still remove the install dir below */ }

        try
        {
            // A running exe can't delete its own folder, so hand the deletion to a detached
            // cmd that waits a moment (ping = portable sleep) for us to exit, then removes it.
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping 127.0.0.1 -n 3 >nul & rd /s /q \"{Program.InstallDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not complete uninstall:\n\n" + ex.Message,
                "JiraLinker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ExitThread();
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
    // Current set of project prefixes -> URL templates, compiled into one regex.
    // Swapped atomically when the user edits projects (read on the same UI thread).
    private ProjectSet _projects;

    // Characters that, typed immediately after a ticket token, suppress linkification
    // (leave the ticket as plain text). Swapped atomically alongside _projects; empty
    // by default so existing installs are unchanged. Whitespace is never included.
    private HashSet<char> _suppress;

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
        var cfg = ConfigStore.Load();
        _projects = ProjectSet.Build(cfg.Projects);
        _suppress = BuildSuppressSet(cfg.SuppressChars);

        _proc = HookCallback;
        _restoreTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _restoreTimer.Tick += (_, _) => { _restoreTimer.Stop(); RestoreClipboard(); };
    }

    /// <summary>Builds the lookup set, dropping whitespace (space is never a suppressor).</summary>
    private static HashSet<char> BuildSuppressSet(string? chars) =>
        new((chars ?? "").Where(c => !char.IsWhiteSpace(c)));

    public void Install()
    {
        using var curProc = Process.GetCurrentProcess();
        using var curMod = curProc.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curMod.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install keyboard hook (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>A copy of the current projects, for display/editing in the UI.</summary>
    public List<Project> GetProjects() => _projects.Projects.Select(p => p.Clone()).ToList();

    /// <summary>The current suppress characters as a string, for display/editing in the UI.</summary>
    public string GetSuppressChars() => new(_suppress.ToArray());

    /// <summary>Applies edited config: recompiles the matcher, swaps the suppress set, saves to disk.</summary>
    public void SetConfig(IEnumerable<Project> projects, string suppressChars)
    {
        var list = projects.ToList();
        _projects = ProjectSet.Build(list);
        _suppress = BuildSuppressSet(suppressChars);
        ConfigStore.Save(new ConfigStore.AppConfig { Projects = list, SuppressChars = suppressChars ?? "" });
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
            var projects = _projects;
            var m = projects.Pattern?.Match(_buf.ToString());
            if (m is { Success: true })
            {
                // A user-configured "special" char right after the token suppresses linking:
                // leave the ticket as plain text and let the char type through normally.
                if (isPrintable && _suppress.Contains(triggerChar))
                {
                    _buf.Clear();
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                string prefix = m.Groups[1].Value.ToUpperInvariant();
                string display = prefix + "-" + m.Groups[2].Value;
                string url = projects.BuildUrl(prefix, display);
                int tokenLen = m.Value.Length;
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

/// <summary>Loads the embedded app.ico at the requested size for the tray and windows.</summary>
static class AppIcons
{
    private static byte[]? _bytes;

    private static byte[] Bytes()
    {
        if (_bytes != null) return _bytes;
        var asm = typeof(AppIcons).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
        if (name != null)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            _bytes = ms.ToArray();
        }
        else _bytes = Array.Empty<byte>();
        return _bytes;
    }

    private static Icon Load(int size)
    {
        var b = Bytes();
        if (b.Length == 0) return (Icon)SystemIcons.Information.Clone();
        using var ms = new MemoryStream(b);
        return new Icon(ms, new Size(size, size));
    }

    public static Icon Tray() => Load(SystemInformation.SmallIconSize.Width); // 16px (DPI-aware)
    public static Icon Window() => Load(32);
}

/// <summary>One project prefix and the URL it links to. {KEY} = the full ticket id.</summary>
sealed class Project
{
    public string Prefix { get; set; } = "";
    public string UrlTemplate { get; set; } = "https://herzog.atlassian.net/browse/{KEY}";
    public Project Clone() => new() { Prefix = Prefix, UrlTemplate = UrlTemplate };
}

/// <summary>Immutable, compiled snapshot of the projects used by the hook for matching.</summary>
sealed class ProjectSet
{
    private const string FallbackTemplate = "https://herzog.atlassian.net/browse/{KEY}";

    public List<Project> Projects { get; }
    public Regex? Pattern { get; }                 // null when there are no projects
    private readonly Dictionary<string, string> _templates; // UPPER prefix -> template

    private ProjectSet(List<Project> projects, Regex? pattern, Dictionary<string, string> templates)
    {
        Projects = projects;
        Pattern = pattern;
        _templates = templates;
    }

    public static ProjectSet Build(IEnumerable<Project> input)
    {
        var projects = new List<Project>();
        var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in input)
        {
            string prefix = (p.Prefix ?? "").Trim();
            if (prefix.Length == 0 || templates.ContainsKey(prefix)) continue; // skip blanks/dupes
            string template = (p.UrlTemplate ?? "").Trim();
            if (template.Length == 0) template = FallbackTemplate;
            projects.Add(new Project { Prefix = prefix, UrlTemplate = template });
            templates[prefix] = template;
        }

        Regex? pattern = null;
        if (projects.Count > 0)
        {
            string alt = string.Join("|", projects.Select(p => Regex.Escape(p.Prefix)));
            pattern = new Regex(@"(?<![A-Za-z0-9])(" + alt + @")-(\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        return new ProjectSet(projects, pattern, templates);
    }

    public string BuildUrl(string prefix, string key)
    {
        string template = _templates.TryGetValue(prefix, out var t) ? t : FallbackTemplate;
        return template.Contains("{KEY}") ? template.Replace("{KEY}", key) : template + key;
    }
}

/// <summary>Loads/saves the project list as JSON in %APPDATA%\JiraLinker\projects.json.</summary>
static class ConfigStore
{
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JiraLinker");
    public static string Location => Path.Combine(Dir, "projects.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Location))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Location), JsonOpts);
                if (cfg?.Projects is { Count: > 0 }) return cfg; // SuppressChars defaults to "" if absent
            }
        }
        catch { /* fall through to defaults */ }

        var defaults = new AppConfig { Projects = Defaults() };
        Save(defaults); // seed on first run, or recover from a corrupt file
        return defaults;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Location, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { /* best effort */ }
    }

    private static List<Project> Defaults() => new()
    {
        new Project { Prefix = "CMMS", UrlTemplate = "https://herzog.atlassian.net/browse/{KEY}" },
        new Project { Prefix = "MCP",  UrlTemplate = "https://herzog.atlassian.net/browse/{KEY}" },
    };

    public sealed class AppConfig
    {
        public List<Project> Projects { get; set; } = new();
        // Chars that suppress linking when typed right after a token. Optional in JSON:
        // System.Text.Json leaves it "" for configs written before this field existed.
        public string SuppressChars { get; set; } = "";
    }
}

/// <summary>Grid UI to add/edit/remove project prefixes and their URL templates.</summary>
sealed class SettingsForm : Form
{
    private readonly DataGridView _grid;
    private readonly TextBox _suppressBox;
    private readonly Action<IEnumerable<Project>, string> _onSave;

    public SettingsForm(List<Project> projects, string suppressChars, Action<IEnumerable<Project>, string> onSave)
    {
        _onSave = onSave;

        Text = "Jira Linker — Projects";
        Width = 660;
        Height = 400;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        Icon = AppIcons.Window();

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(12, 10, 12, 0),
            Text = "Add the project prefixes you want auto-linked (e.g. CMMS, MCP). "
                 + "In the URL, use {KEY} where the ticket id goes — e.g. https://herzog.atlassian.net/browse/{KEY}"
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersWidth = 30,
            BackgroundColor = SystemColors.Window
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "Prefix", FillWeight = 22, MaxInputLength = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        { HeaderText = "URL template  ({KEY} = ticket id)", FillWeight = 78 });

        foreach (var p in projects)
            _grid.Rows.Add(p.Prefix, p.UrlTemplate);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 52,
            Padding = new Padding(10)
        };
        var save = new Button { Text = "Save", Width = 90 };
        var cancel = new Button { Text = "Cancel", Width = 90 };
        var remove = new Button { Text = "Remove selected", Width = 130 };
        save.Click += (_, _) => OnSaveClicked();
        cancel.Click += (_, _) => Close();
        remove.Click += (_, _) => RemoveSelected();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(remove);

        var suppressPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Height = 40,
            Padding = new Padding(12, 8, 12, 8)
        };
        var suppressLabel = new Label
        {
            Text = "Don't link if followed by any of these characters (space excluded):",
            AutoSize = true,
            Margin = new Padding(0, 5, 8, 0)
        };
        _suppressBox = new TextBox { Width = 160, Text = suppressChars ?? "" };
        suppressPanel.Controls.Add(suppressLabel);
        suppressPanel.Controls.Add(_suppressBox);

        // Dock order matters: the Fill control is added first so it takes the
        // remaining space; the two Bottom panels are added last, buttons after
        // suppressPanel so buttons sit at the very bottom and suppressPanel above them.
        Controls.Add(_grid);
        Controls.Add(suppressPanel);
        Controls.Add(buttons);
        Controls.Add(info);

        AcceptButton = save;
        CancelButton = cancel;
    }

    private void RemoveSelected()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (!row.IsNewRow) _grid.Rows.Remove(row);
    }

    private void OnSaveClicked()
    {
        var result = new List<Project>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            string prefix = (row.Cells[0].Value as string ?? "").Trim();
            string url = (row.Cells[1].Value as string ?? "").Trim();
            if (prefix.Length == 0 && url.Length == 0) continue; // ignore fully blank rows

            if (!Regex.IsMatch(prefix, "^[A-Za-z][A-Za-z0-9]*$"))
            {
                Warn($"Invalid prefix: \"{prefix}\".\nUse letters and digits only, starting with a letter (e.g. CMMS).");
                return;
            }
            if (url.Length == 0)
            {
                Warn($"Project \"{prefix}\" needs a URL template.");
                return;
            }
            if (!seen.Add(prefix))
            {
                Warn($"Duplicate prefix: \"{prefix}\". Each prefix can only appear once.");
                return;
            }
            result.Add(new Project { Prefix = prefix.ToUpperInvariant(), UrlTemplate = url });
        }

        // Strip whitespace (space must stay a normal trigger) and de-dupe the suppress chars.
        string suppress = new((_suppressBox.Text ?? "")
            .Where(c => !char.IsWhiteSpace(c)).Distinct().ToArray());

        _onSave(result, suppress);
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Jira Linker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
