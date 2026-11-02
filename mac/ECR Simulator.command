#!/bin/bash
# ECR Simulator for macOS (created by Ahmad El Lahib). Double-click to open the window.
cd "$(dirname "$0")" || exit 1
# Prefer the python.org or Homebrew Python: Apple's /usr/bin/python3 ships an old Tk that can show a blank window.
for py in /Library/Frameworks/Python.framework/Versions/Current/bin/python3 /opt/homebrew/bin/python3 /usr/local/bin/python3 python3; do
    if command -v "$py" >/dev/null 2>&1 && "$py" -c "import tkinter, sys; sys.exit(0 if tkinter.TkVersion >= 8.6 else 1)" 2>/dev/null; then
        exec "$py" ecr_simulator.py "$@"
    fi
done
echo "Python 3 with Tk 8.6 was not found."
echo "Install Python from https://www.python.org/downloads/macos/ (it includes Tk), then double-click this file again."
read -r -p "Press Enter to close."
