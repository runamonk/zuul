# Zuul

A keyboard launcher for Windows. Press Alt+Space, type, press Enter.

Built with .NET 10 and Avalonia 12, the same stack and theme files as Brain Pending.

## What it finds

- **Apps and files from folders**: Settings › Folders starts with both Start menus and both desktops, and you can add
  your own. Their order is their priority: move a folder up and its results rank higher, though a much better name
  match still wins. Each can be switched off, and can include subfolders or not. Only files with the extensions on the File types tab are picked up; the defaults are
  `.exe .lnk .url .appref-ms .bat .cmd .com .msc .cpl`. Shortcuts also have to point at one of those, so a Start menu
  shortcut to a readme stays out. Up to 10,000 files per folder; hidden and system files are skipped.
- **Store apps**: these don't live in a folder, so they have their own switch on the Folders tab.
- The ones you launch most rise to the top.
  "Rebuild index now" there, or typing `reload index`, picks up changes straight away. Otherwise it refreshes every few minutes.
- **Sums**: `12*12`, `sqrt(2)`, `2^10`. Enter copies the answer.
- **Paths**: `C:\Win` lists matching entries in `C:\`. `%APPDATA%` and `~` work.
- **URLs**: `example.com/thing` or `www.example.com`.
- **Commands**: `> ipconfig /all` runs it in Command Prompt.
- **System**: Lock, Sleep, Sign out, Restart, Shut down, Empty Recycle Bin. The risky ones want a second Enter.
- **Web search**: always the last row, unless turned off in settings.

Ctrl+Shift+Enter runs apps, files and commands as administrator.

## Settings

Type `settings`, or right-click the tray icon. Everything lives in `%LOCALAPPDATA%\Zuul`:
`settings.json`, `usage.json`, and `themes.json` (same format as Brain Pending's, so palettes copy across).

## Updates

Release builds check GitHub for a newer version when Zuul starts (Settings › Updates turns that off or spaces it out),
and `update` or the tray menu checks now. Zuul asks before downloading, checks the download's SHA-256, and installs it
when Zuul quits. The replaced files are kept in a `backup` folder next to Zuul.exe. Dev builds never update.

## Build

```
dotnet test Zuul.slnx
publish.cmd
```
