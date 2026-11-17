# OrcaSlicer single-instance hand-off (what OrcaPicker relies on)

Source: `https://github.com/OrcaSlicer/OrcaSlicer`, branch `main`, commit
`07f38df2a4fd3439cffbf991ee41ee99ec71fbee` (2026-11-17). Line numbers refer to that commit.

## 1. Which window receives the message

`src/slic3r/GUI/InstanceCheck.cpp:109-145` (`EnumWindowsProc`): a second Orca instance walks all
top-level windows and picks the first one that

- has window class **`wxWindowNR`** (line 116), and
- carries the window properties **`Instance_Hash_Minor`** and **`Instance_Hash_Major`** (lines 121-124),
  whose combined 64-bit value equals its own instance hash (lines 126-132).

The title is **not** checked. The hash is `std::hash` of the exe's full path (line 321), so every
window of the same `orca-slicer.exe` has the same hash.

The properties are set on the **MainFrame HWND** by `init_windows_properties`
(`InstanceCheck.cpp:445-455`). It's called at the end of `GUI_App::post_init`
(`GUI_App.cpp:1239`) **unconditionally**, whatever the single-instance preference. It's removed in
`shutdown` (`InstanceCheck.cpp:417-419`). So a window that is still starting up does not have the
properties yet.

**OrcaPicker:** lists top-level windows of class `wxWindowNR` that carry `Instance_Hash_Minor`, belong to
an `orca-slicer.exe` process, and are visible. It sends to the window you choose, so it doesn't use
the "first matching hash" rule. `EnumWindows` returns windows in z-order (topmost first), which gives
the "most recently active first" ordering.

## 2. The message

`InstanceCheck.cpp:146-167` (`send_message`):

```cpp
std::wstring wstr = boost::nowide::widen(message);          // UTF-8 -> UTF-16
data_to_send.dwData = 1;
data_to_send.cbData = sizeof(TCHAR) * (wcslen(...) + 1);    // includes the terminator
data_to_send.lpData = wstr.c_str();
SendMessage(orca_slicer_hwnd, WM_COPYDATA, 0, (LPARAM)&data_to_send);
```

`message` is `escape_strings_cstyle(arguments)`, where `arguments = { argv[0], <every other arg> }` with
`--single-instance` / `--no-single-instance` removed (`InstanceCheck.cpp:81-102`).

Receiver: `GUI_App.cpp:839-847` registers a `WM_COPYDATA` handler for every wx window. If
`dwData == 1` it narrows `lpData` (UTF-16, read up to the NUL) to UTF-8 and calls
`OtherInstanceMessageHandler::handle_message`. The handler always returns `true`, so the
`SendMessage` result carries no information. **OrcaPicker** therefore treats only a failure of
`SendMessageTimeout` itself as an error (e.g. `ERROR_ACCESS_DENIED` from UIPI, or a timeout).

## 3. Escaping: `escape_strings_cstyle` / `unescape_strings_cstyle`

`src/libslic3r/Config.cpp:99-148` (escape) and `173-240` (unescape):

- The strings are joined with `;`.
- A string is **quoted** if it contains a space, tab, `;`, `\`, `"`, CR or LF. It's also quoted if it
  is the only string and it's empty.
- Inside quotes, `\` → `\\`, `"` → `\"`, CR → `\r`, LF → `\n`. Everything else is copied byte for
  byte (UTF-8 passes through untouched).
- Unquoted strings are copied as-is.
- Unescape: skips spaces and tabs before and after each item. A quoted item runs to the next
  unescaped `"`, and `\x` becomes `x` (with `\r` and `\n` special). An unquoted item runs to the
  next `;`. A trailing `;` yields one extra empty string. An unterminated quote or a lone trailing
  `\` makes it return `false`.

**OrcaPicker:** `CStyleEscape.cs` ports both functions exactly. The tests round-trip them.

## 4. What the receiver does with the arguments

`InstanceCheck.cpp:503-540` (`handle_message`):

1. It unescapes the message and **skips the first argument** (lines 521-523).
2. For each remaining argument, `get_path` (lines 481-500) checks whether it's an existing path
   (length ≥ 3, optionally wrapped in quotes). If it is, it goes into `paths`.
3. Otherwise, if it matches `^(orcaslicer|prusaslicer|cura|bambustudio)://open[/]?\?file=` or
   `^bambustudioopen://` (case-insensitive, lines 517-518), it goes into `downloads`.
4. Anything else is silently ignored.
5. It posts `EVT_LOAD_MODEL_OTHER_INSTANCE` with `paths` and `EVT_START_DOWNLOAD_OTHER_INSTANCE` with
   `downloads`.

Handlers in `src/slic3r/GUI/Plater.cpp`:

- `8409-8418`: load event. Calls `mainframe->Show(); Raise();` then `Plater::load_files(paths)`.
- `8420-8427`: download event. Calls `Show(); Raise();` then `GUI_App::start_download(url)` for each URL.

### Downloads (`GUI_App.cpp:10450-10467`, `Downloader.cpp:144-186`)

- `start_download` needs Orca's **download folder** preference (`download_path`) to be set.
  Otherwise it shows an error.
- `Downloader::start_download` matches the same regex. It takes **everything after `file=`** and
  percent-decodes it **once** (`FileGet::escape_url`, `DownloaderFileGet.cpp:37-51`, which calls
  `curl_easy_unescape`).
- `bambustudio://...` and `bambustudioopen://...` links, and `orcaslicer://` links whose URL is
  `makerworld.com`, go to `Plater::request_model_download` → `import_model_id`
  (`Plater.cpp:16743`). That understands `<url>&name=<filename>`.
- All other links use Orca's generic downloader, which shows a progress notification and then calls
  `load_files` on the result (`Downloader.cpp:207-215`).

**OrcaPicker:** links are **forwarded to Orca unchanged**, with only the scheme lower-cased, when
decoding the `file=` value once gives an `http(s)://` URL. That is what Orca would have received
directly. Anything else (double-encoded values, odd spellings, embedded URLs) is rebuilt as
`<scheme>://open?file=<encoded URL>`.

### Meshes vs 3MF projects vs zips (`Plater.cpp:19211-...`)

`load_files` accepts `stp|step|stl|oltp|obj|amf|3mf|svg|zip|drc|glb|gltf|fbx` (line 19213). Then:

- **`.zip`** files are opened by Orca itself (`handle_zips` → `preview_zip_archive`, line 19305).
- **`.3mf`**: `choose_3mf_load_type` (line 19201) asks whether to load it as a project or as geometry
  only, depending on Orca's "project load behaviour" preference and whether the plate already has
  objects (line 19323).
- **Plain meshes** are added to the current plate.

## 5. Starting a new window

The plan expected `orca-slicer.exe --no-single-instance <files>`. **That no longer works:**

- `InstanceCheck.cpp:90-95` still recognises the flag, but the CLI parser runs first:
  `CLI::run` → `setup()` (`OrcaSlicer.cpp:1361`) → `DynamicConfig::read_cli`
  (`OrcaSlicer.cpp:8255`, `Config.cpp:1802`).
- The `single_instance` option definition is commented out (`PrintConfig.cpp:13324-13333`), so
  `read_cli` prints `Invalid option --no-single-instance` and Orca exits
  (`Config.cpp:1845-1846`).

The single-instance decision uses `cla.should_send`. That falls back to the
`app.single_instance` preference ("Allow only one OrcaSlicer instance", `Preferences.cpp:1773`),
which defaults to `false` (`AppConfig.cpp:193-194`) (`InstanceCheck.cpp:359-363`).

**OrcaPicker:** starts a new window with a plain `orca-slicer.exe "<item>"`. It reads `app.single_instance`
from `%APPDATA%\OrcaSlicer\OrcaSlicer.conf` (JSON followed by an `# MD5 checksum` line). If the
preference is on, the picker warns that "New window" will reuse an existing window.

At startup, a single URL argument that starts with `orcaslicer://open`, `prusaslicer://open`,
`bambustudio://open`, `bambustudioopen://` or `cura://open` is downloaded (`GUI_App.cpp:960-963`,
`libslic3r/Utils.hpp:323-327`). That check is **case-sensitive**, which is why OrcaPicker
lower-cases the scheme. Several arguments, or file paths, go to `load_files`.

## 6. Orca takes `orcaslicer://` back on every start

`GUI_App.cpp:3475` calls `associate_url(L"orcaslicer")` **unconditionally** at every editor startup.
It does it again whenever Preferences closes (`GUI_App.cpp:9217`). `associate_url`
(`GUI_App.cpp:10409-10434`) writes `HKCU\Software\Classes\orcaslicer` → `URL Protocol` and
`shell\open\command` = `"<orca exe>" "%1"`. No preference turns this off.

`prusaslicer://`, `bambustudio://` and `cura://` are only written when you tick them under
Preferences → "Associate web links to OrcaSlicer" (`Preferences.cpp:1974-1983`, `1584-1588`).

Browsers launch protocol handlers with the explicit `open` verb, so changing the scheme's default
verb is not enough. **OrcaPicker:** an optional logon watcher (`OrcaPicker.exe --watch`, started from
`HKCU\...\Run`) waits on `RegNotifyChangeKeyValue` for the scheme keys and restores the OrcaPicker
command as soon as anything overwrites it. OrcaPicker also re-registers missing schemes every time it
runs.
