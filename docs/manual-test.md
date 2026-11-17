# Manual test checklist

Do these on your PC after `--register`. Keep `%LOCALAPPDATA%\OrcaPicker\log.txt` open: every launch logs
the raw argument, the parsed result, the Orca windows found and what was sent.

Before you start:

- [ ] Orca → Preferences: "Allow only one OrcaSlicer instance" is **off**, and a Download folder is set.
- [ ] Orca → Preferences → "Associate web links": prusaslicer and bambustudio are **unticked**.
- [ ] `OrcaPicker.exe` (settings) shows all three schemes → **OrcaPicker**, and "Watcher: running".

## A. Printables → Orca

On a Printables model page, use **Open in OrcaSlicer** (`orcaslicer://`), then repeat with
**Open in PrusaSlicer** (`prusaslicer://`).

| # | Orca windows open | Expected | ✓ |
|---|---|---|---|
| A1 | 0 | No picker. Orca starts and downloads the model. | |
| A2 | 1 | Picker in front: the file name at the top, "Download from files.printables.com", the window preselected. Enter → that window downloads it and comes to the front. | |
| A3 | 2 | Both windows listed, the one you used last first. Pick the *other* one with ↓ + Enter → it gets the model. | |
| A4 | 1 | Picker → **New window** (or press N) → a second Orca starts and downloads the model. | |
| A5 | any | Picker → Esc → nothing happens. | |
| A6 | 1 | Picker → **Settings…** → settings opens on top. Close it → you're back at the picker, and Enter still sends. | |

Note the raw argument in the log for each button (e.g. `orcaslicer://open/?file=...`). That's the real
format the site sends.

## B. MakerWorld → Orca

**Open in Bambu Studio** (`bambustudio://`).

| # | Orca windows open | Expected | ✓ |
|---|---|---|---|
| B1 | 0 | Orca starts, its "Importing Model" dialog downloads the 3MF. | |
| B2 | 1 | Picker shows the model name (from `&name=`). Enter → Orca imports it. | |
| B3 | 2 | Choose the second window → only that one imports. | |

Bambu Studio no longer opens for these links while OrcaPicker is registered. `--unregister` gives
the scheme back to it.

## C. Fusion 360 → Orca

Set up the custom print utility as in the README.

| # | Orca windows open | Expected | ✓ |
|---|---|---|---|
| C1 | 0 | Orca starts with the exported STL/3MF. | |
| C2 | 1 | Picker shows the export's file name and folder. Enter → the model is added to that window's plate. | |
| C3 | 2 | Choose either window → the model goes there. | |

Check the log line `args=[...]`. If Fusion passes the path without quotes and it contains spaces, the
log shows several arguments, which OrcaPicker joins back together.

## D. File types and window states

| # | Test | Expected | ✓ |
|---|---|---|---|
| D1 | A **3MF project** link (or a Fusion 3MF) into a window that already has objects | Orca asks whether to open it as a project or import the geometry. | |
| D2 | A link to a **.zip** (some Printables "download all" links) | Orca handles the zip itself (preview/import of the parts). Record what it does. | |
| D3 | **Minimize** the target window, then send to it | It's restored and comes to the front with the model. | |
| D4 | Send to a window while Orca is **slicing** or has a dialog open | Either it loads once it's free, or after 10 s "OrcaSlicer didn't respond". No crash. | |
| D5 | Run Orca **as administrator**, send to it | A clear error about administrator mode (UIPI), with "Open in a new window" / "Copy link". | |

## E. Rapid clicks

| # | Test | Expected | ✓ |
|---|---|---|---|
| E1 | 1 window open: click two different models' buttons within a second | First picker shows. Choose → then a second picker for the second model. | |
| E2 | 0 windows open: click two models quickly | Orca starts for the first. The second shows "Waiting for OrcaSlicer to finish starting…", then a picker that includes the new window. | |

## F. Registrations

| # | Test | Expected | ✓ |
|---|---|---|---|
| F1 | Close all Orca windows, start Orca from the Start menu, then click an `orcaslicer://` link | The picker appears. Orca re-registered the scheme at startup and the watcher took it back (the log shows "Watcher: orcaslicer:// had been taken by orca-slicer.exe"). | |
| F2 | Turn the watcher off in settings, repeat F1 | The first link after Orca starts may go straight to Orca (known gap). The next OrcaPicker run takes it back and says so in the picker. | |
| F3 | Settings: untick bambustudio → Register | bambustudio:// points to Bambu Studio again. | |
| F4 | `OrcaPicker.exe --unregister` | All three schemes point to what they did before (`reg query HKCU\Software\Classes\orcaslicer /s` etc.). The watcher is gone from Task Manager and from the Run key. | |
| F5 | Sign out and back in (watcher on) | `OrcaPicker.exe --watch` is running (Task Manager → Details). | |
