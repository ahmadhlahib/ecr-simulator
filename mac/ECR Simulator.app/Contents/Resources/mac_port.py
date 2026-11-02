"""Serial port for macOS (also works on Linux): termios + ioctl, no pyserial.

Created by Ahmad El Lahib. The Nexgo D20 base (ESP32-C3 native USB) reboots when RTS goes to 1
while DTR is 0, so the modem lines are set the moment the port opens and HUPCL is cleared so
closing the port does not toggle them.
"""

import errno
import fcntl
import glob
import os
import re
import select
import struct
import subprocess
import sys
import termios
import time

from ecr_engine import LINE_DRIVER, LINE_ON, PortDead, WriteBlocked

# macOS values, used when this Python's termios module does not export them.
_TIOCMGET = getattr(termios, "TIOCMGET", 0x4004746A)
_TIOCMBIS = getattr(termios, "TIOCMBIS", 0x8004746C)
_TIOCMBIC = getattr(termios, "TIOCMBIC", 0x8004746B)
_TIOCM_DTR = getattr(termios, "TIOCM_DTR", 0x002)
_TIOCM_RTS = getattr(termios, "TIOCM_RTS", 0x004)
_TIOCEXCL = getattr(termios, "TIOCEXCL", 0x2000740D)
_IOSSIOSPEED = 0x80085402 if sys.maxsize > 2 ** 32 else 0x80045402   # macOS: any baud rate

_DEAD = (errno.ENXIO, errno.EIO, errno.EBADF, errno.ENODEV)


class PosixSerial:
    def __init__(self, s, write_timeout=3.0):
        self.write_timeout = write_timeout
        self.fd = os.open(s.port, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
        try:
            at_open = self._lines()
            # Set RTS/DTR first, before anything else talks to the device.
            if s.dtr != LINE_DRIVER:
                self._line(_TIOCM_DTR, s.dtr == LINE_ON)
            if s.rts != LINE_DRIVER:
                self._line(_TIOCM_RTS, s.rts == LINE_ON)
            try:
                fcntl.ioctl(self.fd, _TIOCEXCL)   # nobody else opens the port while we hold it
            except OSError:
                pass
            self._configure(s)
            self.before = "lines at open: %s, now: %s" % (at_open, self._lines())
        except Exception:
            os.close(self.fd)
            raise

    def _configure(self, s):
        a = termios.tcgetattr(self.fd)
        a[0] &= ~(termios.IGNBRK | termios.BRKINT | termios.PARMRK | termios.ISTRIP | termios.INLCR |
                  termios.IGNCR | termios.ICRNL | termios.IXON | termios.IXOFF | termios.IXANY | termios.INPCK)
        a[1] &= ~termios.OPOST
        a[2] &= ~(termios.CSIZE | termios.PARENB | termios.PARODD | termios.CSTOPB | termios.HUPCL |
                  getattr(termios, "CRTSCTS", 0) | getattr(termios, "CCTS_OFLOW", 0) | getattr(termios, "CRTS_IFLOW", 0))
        a[2] |= termios.CLOCAL | termios.CREAD | (termios.CS7 if s.data_bits == 7 else termios.CS8)
        if s.parity in ("O", "E"):
            a[2] |= termios.PARENB | (termios.PARODD if s.parity == "O" else 0)
        if s.stop_bits == 2:
            a[2] |= termios.CSTOPB
        a[3] &= ~(termios.ICANON | termios.ECHO | termios.ECHOE | termios.ECHONL | termios.ISIG | termios.IEXTEN)
        a[6][termios.VMIN] = 0
        a[6][termios.VTIME] = 0
        speed = getattr(termios, "B%d" % s.baud, None)
        if speed is not None:
            a[4] = a[5] = speed
        termios.tcsetattr(self.fd, termios.TCSANOW, a)
        if speed is None:
            if sys.platform != "darwin":
                raise ValueError("baud rate %d is not supported here" % s.baud)
            fcntl.ioctl(self.fd, _IOSSIOSPEED, struct.pack("L", s.baud))
        termios.tcflush(self.fd, termios.TCIOFLUSH)

    def _lines(self):
        try:
            v = struct.unpack("i", fcntl.ioctl(self.fd, _TIOCMGET, struct.pack("i", 0)))[0]
            return "DTR=%d RTS=%d" % (1 if v & _TIOCM_DTR else 0, 1 if v & _TIOCM_RTS else 0)
        except OSError:
            return "unknown"

    def _line(self, bit, on):
        fcntl.ioctl(self.fd, _TIOCMBIS if on else _TIOCMBIC, struct.pack("i", bit))

    def set_rts(self, on):
        self._line(_TIOCM_RTS, on)

    def set_dtr(self, on):
        self._line(_TIOCM_DTR, on)

    def write(self, data):
        end = time.monotonic() + self.write_timeout
        sent = 0
        while sent < len(data):
            left = end - time.monotonic()
            if left <= 0:
                raise WriteBlocked("only %d of %d bytes taken (timeout)" % (sent, len(data)))
            try:
                _, w, _ = select.select([], [self.fd], [], left)
                if not w:
                    continue
                sent += os.write(self.fd, data[sent:])
            except OSError as e:
                if e.errno in _DEAD:
                    raise PortDead(os.strerror(e.errno))
                if e.errno != errno.EAGAIN:
                    raise
        try:
            termios.tcdrain(self.fd)
        except termios.error:
            pass

    def read(self):
        try:
            r, _, _ = select.select([self.fd], [], [], 0)
            if not r:
                return b""
            b = os.read(self.fd, 4096)
        except OSError as e:
            if e.errno == errno.EAGAIN:
                return b""
            if e.errno in _DEAD:
                raise PortDead(os.strerror(e.errno))
            raise
        if not b:
            # Readable but no data: the device is gone (unplugged or rebooted).
            raise PortDead("the device disappeared")
        return b

    def close(self):
        try:
            os.close(self.fd)
        except OSError:
            pass


def _d20_prefixes():
    """usbmodem name prefixes of Espressif USB devices (VID 0x303A = the Nexgo D20 base)."""
    if sys.platform != "darwin":
        return []
    try:
        out = subprocess.run(["ioreg", "-r", "-c", "IOUSBHostDevice", "-l", "-w0"],
                             capture_output=True, text=True, timeout=3).stdout
    except Exception:
        return []
    prefixes = []
    for block in out.split("+-o "):
        if re.search(r'"idVendor" = 12346\b', block):
            loc = re.search(r'"locationID" = (\d+)', block)
            if loc:
                prefixes.append("/dev/cu.usbmodem" + ("%x" % int(loc.group(1))).rstrip("0"))
    return prefixes


def list_ports():
    """[(path, label)] of the serial ports, the D20 base first."""
    skip = ("Bluetooth-Incoming-Port", "debug-console", "wlan-debug")
    paths = sorted(p for p in glob.glob("/dev/cu.*") + glob.glob("/dev/ttyUSB*") + glob.glob("/dev/ttyACM*")
                   if not any(s in p for s in skip))
    d20 = _d20_prefixes()
    ports = []
    for p in paths:
        if any(p.startswith(x) for x in d20):
            label = "Nexgo D20 base (Bluetooth)"
        elif "usbmodem" in p or "ttyACM" in p:
            label = "USB modem (terminal USB or base)"
        elif "usbserial" in p or "ttyUSB" in p or "SLAB" in p or "wchusbserial" in p:
            label = "USB-serial adapter"
        else:
            label = "serial"
        ports.append((p, label))
    ports.sort(key=lambda x: 0 if "D20" in x[1] else 1)
    return ports


def port_exists(path):
    return os.path.exists(path)
