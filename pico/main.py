"""
WheelBridge haptic receiver for the Raspberry Pi Pico (MicroPython).

Listens on USB serial for 3-byte frames from WheelBridge -- 0xAA, large, small
(each 0-255) -- and PWMs two vibration motors accordingly. If no frame arrives
for FAILSAFE_MS the motors are cut, so a crashed PC can't leave them buzzing.

Wiring (per motor, low-side switch):
    GP15 --[1k]-- base/gate of NPN (2N2222/S8050) or logic-level N-MOSFET
    motor between VBUS (5 V) and collector/drain, emitter/source to GND
    flyback diode (1N4001/1N5819) across the motor, stripe toward VBUS
    optional 0.1 uF ceramic across the motor terminals for brush noise
Second motor identical on GP14. Only one motor? Set SINGLE_MOTOR = True and
wire it to GP15; it then gets the stronger of the two channels.

Install: flash MicroPython onto the Pico, then copy this file to it as main.py
(Thonny: File > Save as > Raspberry Pi Pico). Unplug/replug and it runs on boot.
Close Thonny before starting WheelBridge -- only one program can hold the port.
"""

import sys
import select
import time
import micropython
from machine import Pin, PWM

LARGE_PIN = 15
SMALL_PIN = 14
SINGLE_MOTOR = False

SYNC = 0xAA
FAILSAFE_MS = 500
PWM_HZ = 20000       # above hearing range so the motor doesn't whine
MIN_DUTY = 0.30      # ERM motors stall below ~30% -- anything non-zero starts here

# Rumble bytes are arbitrary binary; 0x03 would otherwise be treated as Ctrl-C
# by the REPL and kill this script.
micropython.kbd_intr(-1)

led = Pin("LED", Pin.OUT)
large = PWM(Pin(LARGE_PIN))
small = PWM(Pin(SMALL_PIN))
for pwm in (large, small):
    pwm.freq(PWM_HZ)
    pwm.duty_u16(0)


def drive(pwm, value):
    """value 0-255 -> PWM duty, with a floor so low values still spin the motor."""
    if value == 0:
        pwm.duty_u16(0)
        return
    frac = MIN_DUTY + (1.0 - MIN_DUTY) * (value / 255.0)
    pwm.duty_u16(int(frac * 65535))


def apply(l, s):
    if SINGLE_MOTOR:
        drive(large, max(l, s))
        drive(small, 0)
    else:
        drive(large, l)
        drive(small, s)
    led.value(1 if (l or s) else 0)


poll = select.poll()
poll.register(sys.stdin, select.POLLIN)
stdin = sys.stdin.buffer

last_frame = time.ticks_ms()
motors_on = False
state = 0          # 0 = waiting for SYNC, 1 = waiting for large, 2 = waiting for small
pending_large = 0

while True:
    if poll.poll(20):
        data = stdin.read(1)
        if data:
            b = data[0]
            if state == 0:
                if b == SYNC:
                    state = 1
            elif state == 1:
                pending_large = b
                state = 2
            else:
                apply(pending_large, b)
                motors_on = bool(pending_large or b)
                last_frame = time.ticks_ms()
                state = 0

    if motors_on and time.ticks_diff(time.ticks_ms(), last_frame) > FAILSAFE_MS:
        apply(0, 0)
        motors_on = False
