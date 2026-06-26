# JiraLinker

Type a Jira ticket like `CMMS-2747`, hit space, and it instantly becomes a clickable link to that ticket — **in any app on Windows** (Teams, Outlook, Slack, Word, …).

It's a tiny system-tray app: it watches what you type, and when it sees `CMMS-####` or `MCP-####` followed by **any non-letter/digit character** — a space, Enter, Tab, or any punctuation/symbol (`.`, `,`, `)`, `!`, `?`, `:`, `/`, …) — it replaces the text with a named hyperlink (the text still reads `CMMS-2747`, but it links to `https://herzog.atlassian.net/browse/CMMS-2747`). The character you typed is preserved, so `(CMMS-2747)` or `CMMS-2747!` come out exactly as you'd expect.

---

## Install (for users — no .NET needed)

You just need the prebuilt `JiraLinker.exe` (ask whoever shared this, or grab it from the repo's GitHub Releases).

1. Put `JiraLinker.exe` in any folder, alongside `scripts\install.ps1`.
2. Open **PowerShell** in that folder and run:
   ```powershell
   .\scripts\install.ps1
   ```
   (If you only have the exe, run `.\install.ps1 -ExePath .\JiraLinker.exe`.)

That copies the app to `%LOCALAPPDATA%\JiraLinker`, starts it, and sets it to launch on every login. Look for the **ℹ icon by the clock** — right-click it to pause or exit.

> If PowerShell blocks the script, run it once as:
> `powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1`

### Try it
Open Teams or Outlook, type `CMMS-2747` and press space. It should turn into a clickable **CMMS-2747** link.
*(In plain-text-only boxes like Notepad you'll get the raw URL instead — that's expected.)*

### Uninstall
```powershell
.\scripts\uninstall.ps1
```

---

## Build from source (for developers)

Requires the **.NET SDK 10.0+**.

```powershell
git clone https://github.com/T5-labs/JiraLinker.git
cd JiraLinker
.\scripts\build.ps1
```

Output is a single self-contained `dist\JiraLinker.exe` (~110 MB, bundles the runtime so it runs on any Windows 10/11 machine). Then run `.\scripts\install.ps1` to install your local build.

---

## Manage projects (no rebuild needed)

Right-click the tray icon → **Manage projects…** (or double-click the icon). A grid lets you **add, edit, and remove** project prefixes and the URL each one links to:

| Prefix | URL template ({KEY} = ticket id) |
|--------|----------------------------------|
| CMMS   | `https://herzog.atlassian.net/browse/{KEY}` |
| MCP    | `https://herzog.atlassian.net/browse/{KEY}` |

- Type in the blank bottom row to add a project; select a row and **Remove selected** to delete.
- `{KEY}` is replaced with the full ticket id (e.g. `CMMS-2747`). Different projects can point to entirely different URLs.
- **Save** applies changes immediately — no restart or rebuild.

Settings are stored at `%APPDATA%\JiraLinker\projects.json` (seeded with CMMS + MCP on first run). You can hand-edit that file too.

## Customize (code)

The trigger behavior lives in [`Program.cs`](Program.cs):

- **Trigger** — "any printable non-token character" (see `TryGetPrintableChar(...)` plus Enter/Tab handling in `HookCallback`). Token characters (letters, digits, `-`) are defined in `BufferChar(...)`.
- **Default projects** — `ConfigStore.Defaults()` (used only on first run / if the config is missing).

Rebuild with `scripts\build.ps1` after any code change.

---

## How it works

- A global low-level keyboard hook (`WH_KEYBOARD_LL`) buffers typed characters.
- On a word-boundary key it regex-matches a trailing ticket token.
- On a match it builds a rich hyperlink (CF_HTML + RTF, with the raw URL as a plain-text fallback), puts it on the clipboard, deletes the typed token, pastes the link, then restores your previous clipboard ~300 ms later.
- Runs as a tray app with an Enabled/Exit menu.

## Limitations

- **Rich vs. plain text** — named links appear in apps that accept HTML/RTF paste (Teams, Outlook, Slack, Word, OneNote). Plain-text-only fields (Notepad, terminals) get the raw URL.
- **Elevated windows** — a normal-privilege app can't inject into windows running *as administrator* (Windows security). It simply does nothing there.
- **Windows only**, x64.
