# BrightBridge

Makes the brightness keys on a keyboard actually change the brightness of an
external DDC/CI monitor, on a desktop PC where Windows has no brightness stack
of its own.

Built for a **Dell G3223Q** driven from an **ASUS ROG Strix Morph 96 Wireless**,
but nothing in it is specific to either — any monitor that answers DDC/CI and any
keyboard Raw Input can see will work.

---

## Why this is needed

On a desktop, Windows 10 has no way to change the brightness of an external
monitor. It only knows how to drive an internal laptop panel, through the ACPI /
WMI brightness interface an OEM provides:

```
Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightness
  -> Not supported
```

That is the whole problem in one line. The brightness flyout still appears when
you press the keys, and its slider still animates, but there is no display
registered that can receive the value — it is drawing against nothing.

The monitor itself is perfectly willing to be controlled. It just has to be
addressed over **DDC/CI**, the I²C side-channel that rides along the display
cable, which is what this program does:

```
> BrightBridge.exe --list
Dell G3223Q (DP)   [\\.\DISPLAY1]
    brightness  (0x10)  18 / 100
    contrast    (0x12)  75 / 100
    volume      (0x62)  50 / 100
```

So the job is to catch the key press and translate it into a DDC/CI write.

## How it works

**Catching the key.** Brightness keys are not ordinary keys. They carry no
virtual key code, so `WH_KEYBOARD_LL` hooks and `RegisterHotKey` never see them —
which is why remapping tools, including GearLink, cannot reach them. They arrive
as HID reports, so BrightBridge listens with **Raw Input** (`RIDEV_INPUTSINK`,
so focus does not matter) across every HID collection on the machine, and reads
each report two ways:

- **as decoded usages**, via `HidP_GetUsages` — the correct route for standard
  consumer-page keys such as `0x0C/0x006F` *Display Brightness Increment*
- **as raw bit transitions** — which is how this ASUS keyboard reports keys, on
  vendor page `0xFFC0`, report ID `0x03`, as a 21-byte bitmap where each key owns
  one bit

The second path is the reason this works at all on vendor keyboards, where the
pretty HID usage never appears. Both views are offered when learning a binding,
so you bind whichever one your keyboard actually produces.

**Signalling the display.** `dxva2.dll` (`SetVCPFeature`) writes the MCCS VCP
opcode — `0x10` brightness, `0x12` contrast, `0x62` speaker volume. No Dell code
is involved; DDM does not need to be installed or running.

**Keeping it responsive.** A DDC/CI round trip to this monitor takes ~55 ms, far
too slow to do once per key press while a key is held. All monitor I/O happens on
a worker thread, and rapid presses **coalesce**: only the latest value is written,
so holding the key slews smoothly instead of queueing up dozens of writes. The
on-screen readout updates instantly from the in-memory target rather than waiting
for the monitor.

## Prior art, and why this exists anyway

For a **slider**, use [Twinkle Tray](https://twinkletray.com/) or
[Monitorian](https://github.com/emoacht/Monitorian) — both are free, both speak
DDC/CI, and both are more polished at per-display control than this is.

What they do not do is the thing this was built for. Twinkle Tray's own wiki says
third-party apps *cannot detect the brightness keys on most keyboards*, because
those keys are handled at the driver level and never broadcast to the rest of
Windows, and it recommends rebinding them to an ordinary key combination instead.

That is accurate about the **keystroke** path, and it is exactly the wall you hit
if your keyboard software will not let you rebind those keys. But the keys are not
invisible — they are simply not keystrokes. They are HID consumer-page reports,
and **Raw Input can see them**, which is what this program does:

```
Brightness Up   -> usage:000C/006F   Display Brightness Increment
Brightness Down -> usage:000C/0070   Display Brightness Decrement
```

So BrightBridge is not another brightness slider. It is the missing link between
the keys you already have and the monitor that was always willing to listen.
Run it alongside Twinkle Tray if you want both.

## Install

Nothing to install. `BrightBridge.exe` is a single ~48 KB file with no
dependencies and no runtime to download — it uses the .NET Framework that ships
with Windows 10 and 11. Copy it anywhere, including a USB stick, and run it.

Settings live next to the exe when that folder is writable, so a portable copy
keeps its configuration with it. If it is not writable — `Program Files`, or
read-only media — the ini and log fall back to
`%APPDATA%\BrightBridge\` automatically, and the program still runs.

To rebuild after editing anything under `src\`:

```bat
build.cmd
```

The mark — a core disc ringed by dots, graded along a 45 degree diagonal from
white at the upper-left to 25% grey at the lower-right, then feathered — is
defined once, in `src\Glyph.cs`, and drawn by both the tray icon and the shell
icon so the two cannot drift apart. `build.cmd` generates
`BrightBridge.ico` if it is missing; after changing the glyph, regenerate it
explicitly — optionally with a preview sheet showing every size against dark and
light backgrounds:

```bat
tools\make-icon.cmd preview.png
```

It compiles with the .NET Framework compiler that ships in Windows
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) — no SDK, no NuGet,
no dependencies. The result is a single portable `BrightBridge.exe`.

Run it with no arguments and it sits in the tray. Right-click the tray icon and
tick **Start with Windows** to have it load at logon.

## Setting up your keys

1. Run `BrightBridge.exe` and right-click the tray icon.
2. Choose **Learn a key...**
3. Pick the action (for example `brightness:+step`).
4. Click **Press a key...** and press the key you want to use.
5. Every signature that key produced is listed. The most portable one is
   preselected; pick a different one if you prefer. Click **Save binding**.
6. Repeat for brightness down.

Or discover the codes yourself and write them into the ini by hand:

```bat
BrightBridge.exe --spy 30
```

Press keys and it prints the matcher for each one.

## Command line

```
BrightBridge.exe                       run in the tray (normal use)
BrightBridge.exe --list                list DDC/CI monitors and current values
BrightBridge.exe --caps                dump each monitor's MCCS capability string
BrightBridge.exe --get brightness      read a control
BrightBridge.exe --set brightness 40   write an absolute value
BrightBridge.exe --adjust brightness +5
BrightBridge.exe --spy [seconds]       print what your keys emit
```

`<control>` is `brightness`, `contrast`, `volume`, or `vcpXX` for any raw hex
MCCS opcode your monitor supports. `--caps` shows which ones it does.

## Configuration

`BrightBridge.ini`, next to the exe. Edited by the tray menu, but plain text:

```ini
targets=                  # substring of the monitor name; blank = all DDC/CI monitors
step=10                   # how far one press moves the control
snap=true                 # land on exact multiples of step
osd=false                 # our own readout; Windows draws its own regardless
repeat=true               # hold a key to keep moving
repeatDelayMs=400         # hold this long before repeating
repeatRateMs=250          # 250 = 4 Hz, the rate the Windows flyout advances at

bind=usage:000C/006F => brightness:+step
bind=usage:000C/0070 => brightness:-step
```

### Staying in step with the Windows flyout

The Windows brightness flyout is cosmetic here — it is not reading the monitor,
it is running its own counter. Three settings keep that counter and the real
monitor telling the same story:

- `step=10` matches the 10% the shell moves per press
- `repeatRateMs=250` matches the 4 Hz the shell repeats at while a key is held
- `snap=true` lands on 20 / 30 / 40 rather than carrying an offset forever, so a
  monitor that started life on 18% joins the same grid on the first press

They can still drift apart — changing brightness from the monitor's own buttons
or from `--set` moves one and not the other. Holding a key until it clamps at 0
or 100 resyncs them, since both ends stop in the same place.

If the two advance at visibly different speeds while holding a key, `repeatRateMs`
is the dial: larger is slower.

**Matchers**

| Form | Meaning |
| --- | --- |
| `usage:PPPP/UUUU` | decoded HID usage — page / usage |
| `bit:PPPP/RR/N/MM` | vendor report bit — page / report ID / byte index / bit mask |
| `vk:XX` | virtual key code |

**Actions** are `<control>:<amount>`, where amount is `+step`, `-step`, `+N`,
`-N`, or `=N` for an absolute value. So `volume:+step` drives the monitor's own
speakers, and `brightness:=0` is a one-key blackout.

## Known limitations

- **The stock Windows brightness flyout cannot be suppressed.** It is owned by the
  shell; there is a documented registry switch for the *volume* OSD
  (`EnableVolumeOSD`) but no supported equivalent for brightness. Since it is
  going to appear regardless, BrightBridge defaults to `osd=false` and lets it be
  the readout — see *Staying in step with the Windows flyout* above. Setting
  `osd=true` adds our own readout bottom-centre, which shows the value actually
  written to the monitor rather than the shell's guess.
- **DDC/CI must be enabled in the monitor's OSD menu.** It is, on this monitor —
  that is how `--list` can read values at all.
- **A monitor that is asleep or on another input may not answer.** Failures are
  retried, and a monitor is only written off after ten consecutive ones — see
  below.
- **Every matching monitor moves together.** With `targets` blank, one press steps
  all of them. Each keeps its own value, so displays that start at different
  levels stay offset by that difference. Narrow it with `targets=Dell` if you want
  one display only — but then the keys do nothing for the others. Genuine
  independent per-display control is what Twinkle Tray is for.
- **`vcpXX` writes are unrestricted.** Bindings can address any MCCS opcode the
  monitor exposes, including input source (`0x60`) and power state (`0xD6`). That
  is deliberate, and it means a typo in the ini can switch an input or blank a
  screen. `--caps` shows what a monitor actually supports.
- **Not a driver.** A true OEM integration would register a brightness interface
  in kernel mode so the native Windows slider drove the panel. That needs a
  signed kernel-mode filter driver; this achieves the same user-visible result
  from user space, without one.

## Sleep, blanking and hot-plug

Physical monitor handles go stale when displays sleep, blank or get replugged,
and DDC/CI calls against a display that is powering up fail for a second or two
before working again. Two rules keep that from turning into a dead key:

**Re-enumerate whenever the system moves underneath us.** A rescan is requested
on all of:

| Trigger | Message |
| --- | --- |
| System resume from sleep | `WM_POWERBROADCAST` / `PBT_APMRESUMEAUTOMATIC`, and `SystemEvents.PowerModeChanged` |
| Display switched back on after blanking or power saving | `GUID_CONSOLE_DISPLAY_STATE` power setting notification |
| Resolution or topology change, monitor plugged or unplugged | `WM_DISPLAYCHANGE` |
| Workstation unlocked, session connected | `SystemEvents.SessionSwitch` |
| A HID device arriving or leaving | `WM_DEVICECHANGE` (re-registers Raw Input) |

**Never treat one failure as permanent.** A control is retried about once a
second and only marked unsupported after ten consecutive failures, which is then
reset by the next rescan. The log records both the giving up and the recovery.

All monitor I/O - enumerating, reading, writing and releasing handles - happens
on a single worker thread that owns those handles for their whole life. Nothing
touches DDC/CI from the UI thread, because those calls block for seconds while a
display is powering down, and a frozen message pump during a power transition
gets the process killed by Windows as unresponsive.

If something still goes unresponsive, tray menu > **Monitors** > **Rescan now**,
and send the log.

## Tested on

| | |
| --- | --- |
| Dell G3223Q | DisplayPort, MCCS 2.1, brightness + contrast + speaker volume |
| HP Z27s IPS UHD | MCCS 2.2, brightness + contrast; no speaker volume, correctly reported as unsupported |
| ASUS ROG Strix Morph 96 Wireless | emits standard consumer usages `0x006F` / `0x0070` |

Both monitors answer DDC/CI in ~60 ms. Hot-plugging a second display is handled:
`WM_DISPLAYCHANGE` triggers a rescan.

## On laptops

Untested — no laptop was available — so this is what the code will do, not a
report of what it did.

The keys themselves behave the same: Raw Input sees them regardless of chassis,
and if an OEM utility sends vendor codes instead of standard usages, **Learn a
key** binds those too.

What changes is that a laptop **has** a working brightness stack for its internal
panel, so the internal display keeps being driven natively by Windows while
BrightBridge drives the external DDC/CI monitors. That is usually the wanted
result — one key press, both panels follow.

The failure mode to watch for is **double stepping on the internal panel**. If
that panel also answers `dxva2` brightness calls (some do, many do not), Windows
moves it and BrightBridge moves it again, so it jumps two steps per press while
the external monitor moves one. The fix is to exclude it:

```ini
targets=Dell        # or any substring unique to the external monitor
```

Run `--list` to see whether the internal panel appears with a readable
brightness value. If it does not answer, it is marked unsupported once, logged,
and skipped from then on — no double stepping, nothing to configure.

Two other laptop-specific notes:

- **DisplayLink docks do not pass DDC/CI.** Monitors behind one will not appear in
  `--list` and cannot be controlled. USB-C / Thunderbolt DisplayPort-alt-mode
  docks generally do work.
- **After sleep or undocking**, monitor handles go stale, and are refreshed
  automatically — see *Sleep, blanking and hot-plug* above.

## License

MIT — see [LICENSE](LICENSE).

## Files

```
BrightBridge.exe      the program
BrightBridge.ini      configuration and key bindings
BrightBridge.log      what it did, and any DDC/CI failures
BrightBridge.ico      shell icon, generated from src\Glyph.cs
build.cmd             rebuild
tools\MakeIcon.cs     writes the .ico from the same glyph the tray draws
tools\make-icon.cmd   regenerate the icon after changing the mark
LICENSE               MIT
src\Glyph.cs          the mark, drawn once and shared by tray and shell icon
src\Ddc.cs            DDC/CI transport, coalescing write worker
src\RawInput.cs       Raw Input listener, HID usage + vendor bit decoding
src\Config.cs         ini parsing, binding model
src\Program.cs        tray application, binding dispatch, key repeat
src\LearnForm.cs      the "learn a key" dialog
src\Osd.cs            the on-screen readout
src\Cli.cs            command line modes
```
