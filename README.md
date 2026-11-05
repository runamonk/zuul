# Zuul

A keyboard launcher for Windows. Press Alt+Space, type, press Enter.

Built with .NET 10 and Avalonia 12, the same stack and theme files as Brain Pending.

## What it finds

- **Apps and files from folders**: Settings › Folders starts with both Start menus and both desktops, and you can add
  your own. Their order is their priority: move a folder up and its results rank higher, though a much better name
  match still wins. Each can be switched off, and can include subfolders or not. Only files with the extensions on the File types tab are picked up; the defaults are
  `.exe .lnk .url .appref-ms .bat .cmd .com .msc .cpl`. Shortcuts in the Start menus and desktops also have to point at
  one of those, so an installer's shortcut to its readme stays out. Shortcuts in folders you added all count. Up to 10,000 files per folder; hidden and system files are skipped.
- **Store apps**: these don't live in a folder, so they have their own switch on the Folders tab.
- The ones you launch most rise to the top.
  Zuul watches the folders, so new or renamed files show up within a few seconds. "Rebuild index now" there, or typing
  `reload index`, does it straight away, and it rebuilds every few minutes anyway in case a change was missed.
- **Every file on the computer**: `f: invoice` asks [Everything](https://www.voidtools.com/) (1.4 or 1.5), if it is
  running. Closest names come first. A `\` in the search matches whole paths: `f: tools\zuul`. Results come a batch at a
  time; the last row, or ↓ past it, brings the next batch. Settings › Search changes the prefix, the batch size, how long
  it waits after typing, whether folders count, and which folders are left out (WinSxS, the Recycle Bin and temp files
  to start with).
- **Sums**: `12*12`, `sqrt(2)`, `2^10`. Enter copies the answer.
- **Paths**: `C:\Win` lists matching entries in `C:\`. `%APPDATA%` and `~` work.
- **URLs**: `example.com/thing` or `www.example.com`.
- **Commands**: `> ipconfig /all` runs it in Command Prompt.
- **System**: Lock, Sleep, Hibernate, Sign out, Restart, Shut down, Empty Recycle Bin. The risky ones want a second Enter.
- **Web search**: always the last row, unless turned off in settings.

Ctrl+Shift+Enter runs apps, files and commands as administrator.

Right-click a file, folder or app, or press the Menu key or Shift+F10, for: Open, Run as administrator, Edit with
Notepad++ or VS Code (found where they're installed; Settings › General points them at other copies), Open location (in
Explorer or whatever replaces it), Open target location for shortcuts, Open terminal here, Copy path, Copy name,
Hide from results (undone on Settings › Hidden), and Properties.

## Settings

Type `settings`, or right-click the tray icon. Everything lives in `%LOCALAPPDATA%\Zuul`:
`settings.json`, `usage.json`, and `themes.json` (same format as Brain Pending's, so palettes copy across).

## Updates

Release builds check GitHub for a newer version when Zuul starts (Settings › General turns that off or spaces it out),
and `update` or the tray menu checks now. Zuul asks before downloading, checks the download's SHA-256, and installs it
when Zuul quits. The replaced files are kept in a `backup` folder next to Zuul.exe. Dev builds never update.

## Build

```
dotnet test Zuul.slnx
publish.cmd
```
