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
Device Manager shows the device as healthy. An existing community tool
([Phoenix6640/Thrustmaster-458-spider-pc](https://github.com/Phoenix6640/Thrustmaster-458-spider-pc))
built on those APIs is a closed-source binary with no published code, and
flapped between "detected"/"not detected" for exactly this reason.

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

## Known limitations

- No force feedback (this wheel doesn't have an FFB motor).
- Games see it as an Xbox 360 gamepad, so some sims apply gamepad-style
  steering filters/deadzones — look for a "direct"/"raw" input mode if
  steering feels off.
- Only tested with one wheel connected at a time.
