# ApexChime

ApexChime is a Windows 11 desktop timer and daily reminder app for sim racing. Its main purpose is to deliver a short alert without taking focus away from the game. Keep all app-owned UI text in English.

The decisions below describe the current behavior, including the follow-up changes from the 2026-11-21 session.

## Product behavior

- **Preset timers** are 3, 5, 8, 10, 15, 20, 25, 30, 45, and 60 minutes. They appear in two rows of five. Clicking one starts a new timer immediately. The schedule supports multiple active timers without a fixed count limit.
- **Custom timers** start via **Run** or Enter. Accept `15` for minutes, `5:23` for hours and minutes, and `!14:00` for the next local occurrence of a clock time. The duration must be positive and at most 24 hours. Seconds are display-only.
- Hours and minutes accept a non-digit separator for both timers and reminders. Keep colons in examples and UI hints. A timer may include a description after a space, such as `0:15 Check fuel`. If whitespace is followed by a valid two-digit minute field after an eligible hour, it is interpreted as the time separator. Use an explicit colon in the duration when the description could be mistaken for minutes, such as `0:15 30 laps`. Without a description, use `NN min timer`. For `!HH:MM`, the generated title rounds the computed duration up to a whole minute.
- Create **daily reminders** via **Add** or Enter. Accept a local clock time from `00:00` to `23:59` and an optional message of up to 30 characters. A blank message displays as **Daily reminder**. Adding a reminder for the current minute schedules its first occurrence for the next day. Deleting a reminder removes the recurrence permanently.
- **Schedule** shows timers and the next occurrence of each daily reminder in one scrollable list sorted by due time. Timers use an hourglass icon and reminders use a finger-with-a-knot icon. Each row has a delete button. Due times use the current Windows time zone in 24-hour `HH:mm` format.
- Remaining time updates once per second. Use `MM:SS` through exactly one hour and `HH:MM:SS` above one hour. Completed timers show **Done** for 30 seconds from their due time. Daily reminders advance to the next occurrence after firing.
- **Alerts** use a translucent ApexChime banner at the bottom right of the foreground monitor. The banner is topmost, click-through, and non-activating. It expires after 15 seconds per alert. Overlapping alerts share the banner and display an additional-alert count. The selected sound plays once per batch. Windowed and borderless games can show the banner.
- The footer contains **Autostart**, **Notifications**, **Sound**, and the version label. **Notifications** controls both the visual banner and scheduled audio. **No sound** keeps visual alerts enabled and mutes audio. Selecting a different sound previews it immediately.
- Closing the main window hides it to the Windows notification area. Double-click the tray icon or select **Open ApexChime** to reopen it. **Exit** quits. A second normal launch raises the existing window. Autostart uses `--background` to keep the window hidden at sign-in.
- Restore the main window's size, position, and maximized state on restart. If the saved monitor position is unavailable, open on an available monitor. The default size is 642 × 820 WPF device-independent units. The minimum size is 550 × 670.

## Data and scheduling

- Fresh settings enable notifications, select **Windows default**, and include one daily reminder named `go to sleep` at `22:00`. Autostart is off by default.
- `StateStore.cs` reads and writes `%LOCALAPPDATA%\ApexChime\settings.json`. It serializes `SavedState` as JSON with named enum values. The saved data includes the notification preference, sound, optional window placement, timers, and recurring reminders. State writes flush a sibling temporary file and replace the saved file. The main window retries on recoverable save failures.
- A timer has an ID, an absolute `DateTimeOffset` due time, a title, and a delivery flag. A daily reminder has an ID, local hour and minute, a message, and the most recent local date on which it fired. Preserve saved timers, reminders, and settings from earlier builds when updating the schema.
- Relative timers calculate their deadline from UTC. Finish-time timers resolve the next matching local instant within 24 hours, including the second occurrence of an ambiguous time when applicable. Daily reminders follow the current system time zone. They use the first valid minute after a daylight-saving gap and the first occurrence of a repeated local time. An alert that is already 30 seconds old is skipped on startup or resume.
- Window placement stores physical screen coordinates, WPF units for the normal window size, and a maximized flag. Existing settings without placement use the defaults.
- Autostart is a per-user `ApexChime` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The value contains the quoted executable path followed by `--background`. Refresh the registration by toggling **Autostart** off and on after moving the executable.

## Sounds and assets

- Current sound choices are **Windows default**, **Glass chime**, **Confirmation**, **Digital ping**, **Low bong**, **Pluck**, **Critical stop**, and **No sound**.
- In the tested Windows sound scheme, **Information** and **Exclamation** used the same WAV as **Windows default**, and **Question** had no sound. They were removed from the menu. Keep the corresponding enum names readable for older JSON. Loading any of those choices selects and saves **Windows default**.
- The five bundled clips are PCM WAV conversions from Kenney's **Interface Sounds** pack, licensed CC0 1.0. Match the menu labels to `glass_001.wav`, `confirmation_001.wav`, `select_003.wav`, `bong_001.wav`, and `pluck_002.wav`. These 0.12–0.38 second clips are embedded in the executable and use the same playback path for previews and alerts. See [the upstream pack](https://kenney.nl/assets/interface-sounds) and [the included license](Assets/Sounds/LICENSE.txt).
- The app icon combines an hourglass with a racing corner and its apex. `Assets/ApexChime.ico` is used for the executable and tray. `Assets/ApexChime.png` is used in the WPF interface. `Assets/screenshot.png` shows sample timers and reminders in the README.

## Code and distribution

| File | Responsibility |
| --- | --- |
| `ApexChime.csproj`, `app.manifest` | WPF on `net10.0-windows` with Windows Forms for tray and monitor APIs. Version `0.0.1`, authors Nikita Kovalev and codex, identity `dev.maizy.ApexChime`, and per-monitor DPI awareness. |
| `App.xaml`, `App.xaml.cs` | Shared styles, startup and shutdown, tray, single-instance handoff, Windows clock-change handling, and autostart registration. |
| `MainWindow.xaml`, `MainWindow.xaml.cs` | Input controls, schedule rows and countdown display, settings UI, and window placement. |
| `Schedule.cs` | Saved data model, parsing, deadline calculations, recurrence, delivery, and ordering. |
| `StateStore.cs` | Local JSON storage. |
| `Alerts.cs`, `AlertWindow.xaml` | Sound options and playback, alert lifetime and grouping, banner, and Win32 window interop. |
| `build.ps1`, `.github/workflows/build.yml`, `CHANGELOG.md` | Portable builds, GitHub Actions artifacts, and tag-driven GitHub releases with changelog notes. |

Use the [.NET 10 SDK for Windows](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) to develop or build. Publish both architectures from the repository root in PowerShell.

```powershell
.\build.ps1 -Architecture x64
.\build.ps1 -Architecture arm64
```

The outputs are `dist/win-x64/ApexChime.exe` and `dist/win-arm64/ApexChime.exe`. Each is one self-contained executable with sound assets and the runtime included. The app needs no installer or separately installed runtime. If a running executable prevents replacement, publish to a fresh directory so existing timers can continue running.

GitHub Actions uses `windows-2025` and the .NET 10 SDK to build artifacts for both architectures on pushes, pull requests, and manual runs. Pushing a `vX.Y.Z` tag runs a release job after both builds succeed. The tag must match the project version in `ApexChime.csproj`. Update the manifest version alongside the project version and add a matching `## vX.Y.Z` section to `CHANGELOG.md` for curated notes. GitHub generates notes if the section is absent. The release assets are `ApexChime-win-x64.exe` and `ApexChime-win-arm64.exe`. Keep these names stable for the README download links.

The repository code is MIT. The bundled audio has the separate CC0 notice above. Keep `bin/`, `obj/`, and `dist/` out of source control.

Keep the public README brief, with the screenshot near the top. The user asked to omit the app version, reminder clock-format and message-length details, the first-run reminder, schedule sorting details, and the location of settings from the README. Those details belong here.

## Verification and session handoff

- The first commit was `1044a74` (`Initial ApexChime release`). The timer, window, and sound refinements followed in `9568551` (`Refine timers, window placement, and alert sounds`).
- Built both x64 and Arm64 as single-file executables and checked their PE architecture headers. Windows runtime checks were on x64. Arm64 execution was not verified.
- Local smoke checks covered parsing and time separators, descriptions, midnight rollover and repeated daylight-saving times, daily recurrence and deletion, JSON persistence, the one-hour display boundary, normal and maximized window restoration, unavailable monitor placement, sound selection and migration, the 15-second alert lifetime without focus transfer, and the 30-second completed-row lifetime. A portable x64 smoke test also covered background startup, second-launch handoff, and closing to the tray.
- The session used temporary verification projects and isolated settings under `obj/`. They are not a tracked automated test suite. For future verification, use isolated settings and leave the user's live reminders and autostart registration intact.
