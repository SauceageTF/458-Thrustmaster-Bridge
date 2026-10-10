# WheelBridge

Makes the Thrustmaster Ferrari 458 Spider Racing Wheel (an Xbox 360-licensed
accessory, USB `044F:B671`) usable in PC games, by reading its raw USB reports
directly and re-publishing them as a virtual Xbox 360 controller via ViGEmBus.

## Why this exists

This wheel never registers with Windows' `Windows.Gaming.Input`/XInput/GIP
stack — Windows binds it to the `dc1-controller` (Xbox accessory) driver, which
claims the device but never completes whatever capability negotiation those
APIs require, so `RacingWheel.RacingWheels`, `Gamepad.Gamepads`, and
`RawGameController.RawGameControllers` all report zero devices even though
Device Manager shows the device as healthy.

[Phoenix6640/Thrustmaster-458-spider-pc](https://github.com/Phoenix6640/Thrustmaster-458-spider-pc)
did a good job of getting this wheel working on PC first. It's built on the
Windows.Gaming.Input APIs above, though, which is why it can flap between
"detected" and "not detected" on some setups, and since it's distributed as a
prebuilt binary its approach couldn't be adapted here. So this project takes a
different route from the ground up.

The fix used here: rebind the device to **WinUSB** (via Zadig) so Windows'
Xbox accessory driver never touches it, then talk to it directly over raw USB.
The wheel speaks **GIP** (Gaming Input Protocol — it declares the
`MS_COMP_XGIP10` compatible ID) over unencrypted vendor-specific interrupt
transfers, no Xbox Security Method 3 / crypto handshake needed. It does,
however, require the standard GIP handshake: on connect it sends an Announce
frame and then sits idle until the host sends Power On, LED Mode, and a
Serial Number request, at which point it starts streaming real Input frames.
Skip the handshake and every read just returns the same idle Announce frame
forever, which looks like a "detected but frozen" wheel.

## One-time machine setup

1. Install [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases).
2. Install [Zadig](https://zadig.akeo.ie/). Options -> List All Devices, select
   the wheel (shows as "Xbox Gaming Device" or by name once rebound), pick
   **WinUSB** as the target driver, click Replace Driver.
   - This only needs to be done once per machine/USB port; Windows remembers
     the driver binding for that device.
   - To undo: Device Manager -> the device (now under its own class, or
     "Universal Serial Bus devices") -> Update driver -> revert, or just let
     Windows re-claim it as an Xbox accessory (unplug/replug after uninstalling
     the WinUSB driver).

## Running

```
dotnet run
```

or for a standalone exe:

```
dotnet publish -c Release -r win-x64 --self-contained
```

Keep the console window open while gaming — closing it disconnects the
virtual controller. It auto-detects the wheel and ViGEmBus on startup and
retries every couple seconds if either isn't ready yet or the wheel gets
unplugged.

## Calibration and tuning

SETTINGS -> CALIBRATION -> **Calibrate...** walks through center, full left,
full right, throttle and brake, and records the raw values at each limit, so
full physical lock and a floored pedal reach 100% in-game. You can advance
each step with the wheel's **A** button, so your hands stay on the wheel.
Calibration is kept through "Restore defaults"; use **Reset** to drop it.

Steering, throttle and brake each have a deadzone and a response curve
(1.0 = linear). A live graph above each set of sliders plots physical input
against what the game receives, with a dot tracking the wheel/pedal in real
time. Deadzones are rescaled, so output ramps up from zero at their edge
rather than jumping.

## Profiles

Each profile keeps a separate set of tuning per game: steering
sensitivity/deadzone/curve/invert, throttle and brake deadzone/curve, and
rumble intensity. Switch with the **PROFILE** dropdown at the right of the
tab bar (on either tab) or from the tray icon's **Profile** menu.

SETTINGS -> PROFILE manages them: **New** starts a profile as a copy of the
current one and names it "Profile N" (type over it to rename, or press Enter
to keep it). **Rename** and **Delete** act on the active profile, and there's
always at least one.

Calibration, the rumble output device and app options describe the hardware
or the app rather than a game, so they're shared by every profile. Switching
profiles saves immediately, so tweaks made to the profile you're leaving
aren't lost. Settings files from before profiles existed load as "Profile 1".

## Updates

With "Check for updates at startup" on (SETTINGS -> APP, on by default),
WheelBridge asks the GitHub releases API once per launch whether a newer
version exists, and if so shows a banner on the STATUS tab (plus a tray
notification when started minimized) linking to the release. It never
downloads or installs anything itself. "Check now" runs the same check on
demand. Releases are compared against `<Version>` in `WheelBridge.csproj`,
so bump that (and `MyAppVersion` in `installer/WheelBridge.iss`) together.

## Protocol (reverse engineered)

Every packet on interrupt endpoint `0x81` starts with a 4-byte GIP header:

| Offset | Field |
|---|---|
| 0 | command (`0x02`=Announce, `0x20`=Input; see `WheelBridgeService.cs` for the rest) |
| 1 | low nibble = device ID, high nibble = frame type |
| 2 | sequence number |
| 3 | payload length |

On connect, the wheel sends an **Announce** (`0x02`) frame and then goes
idle. The host must reply on endpoint `0x01` with three GIP commands, in
order, before it'll send real input:

1. Power Mode: On (`0x05`)
2. LED Mode: On (`0x0A`)
3. Request Serial Number (`0x1E`)

See `SendGipHandshake` in `WheelBridgeService.cs` for the exact bytes. Once
handshaken, the wheel streams 21-byte **Input** (`0x20`) frames. Payload
layout (offsets relative to the start of the packet, i.e. including the
4-byte header):

| Offset | Field |
|---|---|
| 4 | button bitmask A: `0x04`=Start, `0x08`=Back, `0x10`=A, `0x20`=B, `0x40`=X, `0x80`=Y |
| 5 | button bitmask B: `0x01`=D-pad Up, `0x02`=Down, `0x04`=Left, `0x08`=Right, `0x10`=left paddle, `0x20`=right paddle |
| 6-7 | LE16 wheel position, centered at `0x8000` |
| 8-9 | LE16 throttle, 0 at rest, ~0x3FF at full press |
| 10-11 | LE16 brake, same range |

All other bytes are always zero in this configuration (no clutch pedal was
available to test).

## Optional: rumble

The wheel has no motor, but games still send Xbox 360 rumble to the virtual
controller. WheelBridge forwards that to one of two outputs (SETTINGS tab ->
RUMBLE OUTPUT). Rumble strength shows live on the STATUS tab under RUMBLE, and
"Test motors" pulses whichever output is connected.

### Xbox controller strapped to the wheel (default, no parts needed)

Plug any spare wired Xbox controller into the PC and zip-tie it to the back of
the wheel hub. WheelBridge re-sends the game's rumble to it via XInput; the
badge reads `RUMBLE -> XBOX #n`. It never rumbles the virtual wheel itself
(that would echo back as new feedback).

One thing to get right: games treat the *first-connected* XInput device as
player 1. Start WheelBridge and let the WHEEL badge go green **before**
plugging in the spare controller, or just unplug/replug it afterwards. If a
game steers with the spare controller's stick, that's the cause.

### Raspberry Pi Pico driving bare motors

For a stronger or more compact setup: a Pico on USB serial PWMs one or two
vibration motors (a salvaged controller rumble motor or coin ERM works well).

**Wiring** (per motor, low-side switch):

```
GP15 --[1k]-- base of NPN (2N2222/S8050) or gate of logic-level N-MOSFET
motor between VBUS (5V) and collector/drain; emitter/source to GND
flyback diode (1N4001/1N5819) across the motor, stripe toward VBUS
```

Second motor the same on GP14. GP15 is the "large" (low-frequency) motor,
GP14 the "small" one. Only have one motor? Set `SINGLE_MOTOR = True` in
`pico/main.py` and it gets the stronger of the two channels.

**Pico setup:**

1. Flash MicroPython (hold BOOTSEL while plugging in, drop the `.uf2` from
   micropython.org onto the RPI-RP2 drive).
2. Copy `pico/main.py` to the Pico as `main.py` (Thonny: File -> Save as ->
   Raspberry Pi Pico). Unplug/replug; it runs on boot.
3. Close Thonny. Pick "Raspberry Pi Pico" under RUMBLE OUTPUT with the port left
   on `auto`; WheelBridge finds the Pico by its USB vendor ID and the badge on the
   STATUS tab turns green. Hit "Test motors" to confirm the wiring.

Protocol, in case you want a different receiver: 115200 8N1, 3-byte frames
`0xAA, large, small` (each 0-255), sent on every change and as a 5 Hz
keepalive. The Pico cuts the motors if it hears nothing for 500 ms.

## Known limitations

- No force feedback (this wheel doesn't have an FFB motor). The rumble
  outputs above are vibration only, not steering force.
- Games see it as an Xbox 360 gamepad, so some sims apply gamepad-style
  steering filters/deadzones — look for a "direct"/"raw" input mode if
  steering feels off.
- Only tested with one wheel connected at a time.
