# Todays Contracts 

A tiny Windows app: one task per day, punch it when it's done, set tomorrow's before you stop.

## Files
- `OneCheck.cs` - all of the app's code (one file)
- `app.ico` - the icon
- `sounds/boot.wav` - the default startup sound, built into the exe (swap it and rebuild to change the default). The punch sound is generated in code: `MakeClassicPunch()`
- `build.bat` - builds `OneCheck.exe` using the C# compiler built into Windows (nothing to install)
- `OneCheck.csproj` - optional, if you'd rather use `dotnet build`
- `.vscode/tasks.json` - lets you build from VS Code with Ctrl+Shift+B

## Build
In VS Code: open this folder, press **Ctrl+Shift+B**.
Or double-click `build.bat`.

Close One Check before rebuilding, or Windows won't let the new exe overwrite the running one.

Note: the Windows built-in compiler only understands C# 5 (2012-era syntax). If you want newer
C# features (`$"..."` strings, `?.`, etc.), build with `dotnet build -c Release` instead;
the exe lands in `bin\Release\net48\`.

## Easy things to change
Search `OneCheck.cs` for these:
- **Accent colors** - the `Accents` list (name + hex color); add or remove as many as you like
- **Max steps per day** - `MaxSteps`
- **Sounds** - punch/startup: replace the files in `sounds/` (or pick any WAV in the app's Settings, no rebuild needed). Interface blips: the `Make...()` methods in the `Sfx` class
- **Theme colors** - `ApplyTheme()`
- **Fonts** - `MakeFonts()`
- **Punch sound** - `MakePunchWav()` (the thump pitch, click, and metal tick)
- **Streak milestones** - `PaintStamp()`, the line with `streak == 3 || streak == 7 ...`
- **Button text** - search for `"PUNCH IT"`, `"Set it"`, `"Save"`
- **Window size** - `LW` and `LH` (logical pixels; they scale with your display)

## Your data
Stored in `%APPDATA%\OneCheck`:
- `days.txt` - one line per day: date, done (1/0), time, task (tab-separated)
- `steps.txt` - today's steps, one line each: date, done (1/0), text (tab-separated)
- `settings.txt` - accent, theme, sound, always-on-top, window position
