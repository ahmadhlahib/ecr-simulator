ECR Simulator 2.0 for macOS  -  created by Ahmad El Lahib
==========================================================

Same simulator as the Windows version, for Mac laptops (Python + Tk, no installation of the app itself).

One-time setup
1. Install Python 3 from https://www.python.org/downloads/macos/ (it includes Tk 8.6).
   Apple's built-in python3 has an old Tk that can show a blank window; Homebrew works too
   (brew install python python-tk).
2. The first time, right-click "ECR Simulator.command" > Open > Open (macOS asks once because the file
   was downloaded). If it says it is not executable, open Terminal in this folder and run:
       chmod +x "ECR Simulator.command"

Start: double-click "ECR Simulator.command"   (or in Terminal: python3 ecr_simulator.py --port /dev/cu.usbmodem1101)

Ports on a Mac
- Nexgo D20 Bluetooth base: /dev/cu.usbmodem...  (shown as "Nexgo D20 base (Bluetooth)" when detected)
- USB-RS232 adapter: /dev/cu.usbserial-... (FTDI / Prolific), /dev/cu.SLAB_USBtoUART (CP210x),
  /dev/cu.wchusbserial... (CH340; may need the WCH driver on older macOS)
- Press Refresh after plugging a cable in.

Important - D20 base on a Mac (NOT TESTED YET)
The D20 reboots when RTS goes to 1 while DTR is 0. macOS may raise both lines for a moment when the port
opens, before the simulator can lower them. The log line "opened ... (lines at open: DTR=.. RTS=..)" shows
what macOS did. If the D20 reboots on open (the log shows "D20 BASE REBOOTED", or the terminal loses the
base), use "Always open" mode so the port opens only once, or use the Windows version for the D20.
Direct cables are not affected.

Everything else (formats, modes, transactions, free text, fault tests, logs) works as described in the
main README.txt one folder up.

Tests (no device needed):  python3 -m unittest test_ecr_engine
