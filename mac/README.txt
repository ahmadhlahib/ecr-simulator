ECR Simulator 2.1 for macOS  -  created by Ahmad El Lahib
==========================================================

Same simulator as the Windows version, as a Mac app: double-click "ECR Simulator.app".

First time only
1. Install Python 3 from https://www.python.org/downloads/macos/ (free; it includes everything the app needs).
   If Python is missing, the app tells you and opens the download page.
2. Optional: drag "ECR Simulator.app" into Applications (and onto the Dock).
3. macOS blocks apps downloaded from the internet that are not from the App Store the first time:
   - macOS 14 or older: right-click the app > Open > Open.
   - macOS 15 (Sequoia) or newer: double-click it, click "Done", then System Settings > Privacy & Security,
     scroll down to "ECR Simulator was blocked" and click "Open Anyway".
   - Or once in Terminal:  xattr -cr "/Applications/ECR Simulator.app"   (use the path where the app is)
After that, a double-click opens it directly.

Logs and settings: Documents/ECR Simulator (logs folder, ecr-simulator.json). The "Open logs folder" button opens it.
If the app does not open, look at Documents/ECR Simulator/startup-errors.log.

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

For developers
- The code is inside the app: ECR Simulator.app/Contents/Resources (ecr_engine.py, mac_port.py, ecr_simulator.py).
- Run from Terminal: python3 "ECR Simulator.app/Contents/Resources/ecr_simulator.py" --port /dev/cu.usbmodem1101
- Tests (no device needed): python3 -m unittest test_ecr_engine
