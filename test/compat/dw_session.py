#!/usr/bin/env python3
"""
Run a real direwolf process without radio hardware and talk to it through its
client interfaces, for end to end interface tests.

  * Receive audio comes from stdin ("ADEVICE stdin ...") as raw 16 bit samples.
  * Transmit audio goes to a file through ALSA's built in "file" plugin
    (slave "null"), so no sound card is needed:  ADEVICE stdin file:FILE=x,FORMAT=raw
  * KISS TCP, AGW and serial / pty KISS are used as a client program would.

Used by interfaces.py.  Linux only (ALSA).
"""

import os
import shutil
import socket
import struct
import subprocess
import tempfile
import threading
import time
import wave

import ax25ref


def wav_samples(path):
    """Raw sample bytes and sample rate of a mono 16 bit WAV file."""
    with wave.open(path) as w:
        assert w.getnchannels() == 1 and w.getsampwidth() == 2
        return w.readframes(w.getnframes()), w.getframerate()


def wait_port(port, timeout=20.0):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            s = socket.create_connection(("127.0.0.1", port), timeout=1)
            return s
        except OSError:
            time.sleep(0.1)
    raise RuntimeError("port %d did not open" % port)


class Direwolf:
    """
    A direwolf process.  conf_lines is the configuration file content.
    tx_file: path for transmit audio (raw S16LE at the configured rate) or None.
    stdin_audio: True to feed receive audio through stdin, else stdin is idle.
    """

    def __init__(self, exe, conf_lines, workdir, name="dw", extra_args=()):
        self.exe = exe
        self.workdir = workdir
        self.name = name
        os.makedirs(workdir, exist_ok=True)
        # Data files direwolf looks for in its working directory.
        data = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "data")
        for fn in ("tocalls.yaml", "symbols-new.txt", "symbolsX.txt"):
            src = os.path.join(data, fn)
            if os.path.exists(src) and not os.path.exists(os.path.join(workdir, fn)):
                shutil.copy(src, workdir)
        self.conf = os.path.join(workdir, name + ".conf")
        with open(self.conf, "w") as f:
            f.write("\n".join(conf_lines) + "\n")
        self.log_path = os.path.join(workdir, name + ".log")
        self.log = open(self.log_path, "wb")
        # direwolf complains for 15 seconds when run as root.  If DW_RUN_AS names
        # a user, drop to it (the executable and workdir must be accessible to it).
        prefix = []
        user = os.environ.get("DW_RUN_AS")
        if user and os.geteuid() == 0:
            prefix = ["setpriv", "--reuid=" + user, "--regid=" + user, "--clear-groups"]
            os.chmod(workdir, 0o777)
        # Line buffered output so the log can be watched while it runs.
        if shutil.which("stdbuf"):
            prefix = prefix + ["stdbuf", "-oL", "-eL"]
        # -t 0: no color codes.
        self.p = subprocess.Popen(prefix + [exe, "-t", "0", "-c", self.conf] + list(extra_args),
                                  stdin=subprocess.PIPE, stdout=self.log, stderr=subprocess.STDOUT,
                                  cwd=workdir)

    def feed(self, raw, chunk=8192, close=True):
        for i in range(0, len(raw), chunk):
            self.p.stdin.write(raw[i:i + chunk])
        self.p.stdin.flush()
        if close:
            self.p.stdin.close()

    def wait(self, timeout=120):
        try:
            return self.p.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            self.stop()
            return None

    def stop(self):
        if self.p.poll() is None:
            self.p.terminate()
            try:
                self.p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.p.kill()
                self.p.wait()
        try:
            self.p.stdin.close()
        except Exception:
            pass
        self.log.close()

    def output(self):
        with open(self.log_path, "rb") as f:
            return f.read().decode("latin-1")


class KissClient:
    def __init__(self, port):
        self.s = wait_port(port)
        self.s.settimeout(0.2)
        self.dec = ax25ref.KissDecoder()
        self.frames = []          # (port, cmd, payload)

    def send(self, frame, chan=0, cmd=0):
        self.s.sendall(ax25ref.kiss_encode(frame, chan, cmd))

    def poll(self, duration):
        t0 = time.time()
        while time.time() - t0 < duration:
            try:
                d = self.s.recv(65536)
                if not d:
                    return False
                self.frames += self.dec.feed(d)
            except socket.timeout:
                pass
        return True

    def drain_until_closed(self, timeout=120):
        t0 = time.time()
        while time.time() - t0 < timeout:
            if not self.poll(0.5):
                return True
        return False

    def close(self):
        self.s.close()


class AgwClient:
    """Minimal AGWPE client: raw frame monitoring ('k') and frame sending ('K')."""

    HDR = struct.Struct("<B3xB3x10s10sII")

    def __init__(self, port):
        self.s = wait_port(port)
        self.s.settimeout(0.2)
        self.buf = b""
        self.msgs = []            # (port, kind, call_from, call_to, data)

    def send(self, port, kind, call_from=b"", call_to=b"", data=b""):
        self.s.sendall(self.HDR.pack(port, ord(kind), call_from.ljust(10, b"\0"),
                                     call_to.ljust(10, b"\0"), len(data), 0) + data)

    def poll(self, duration):
        t0 = time.time()
        while time.time() - t0 < duration:
            try:
                d = self.s.recv(65536)
                if not d:
                    return False
                self.buf += d
            except socket.timeout:
                pass
            while len(self.buf) >= 36:
                port, kind, cf, ct, dlen, _ = self.HDR.unpack(self.buf[:36])
                if len(self.buf) < 36 + dlen:
                    break
                self.msgs.append((port, chr(kind), cf.rstrip(b"\0"), ct.rstrip(b"\0"), self.buf[36:36 + dlen]))
                self.buf = self.buf[36 + dlen:]
        return True

    def drain_until_closed(self, timeout=120):
        t0 = time.time()
        while time.time() - t0 < timeout:
            if not self.poll(0.5):
                return True
        return False

    def close(self):
        self.s.close()


def free_port():
    """A free TCP port below 49152 (direwolf rejects higher ones)."""
    import random
    while True:
        p = random.randint(20000, 45000)
        s = socket.socket()
        try:
            s.bind(("127.0.0.1", p))
            return p
        except OSError:
            continue
        finally:
            s.close()


def tmpdir(base, prefix):
    os.makedirs(base, exist_ok=True)
    return tempfile.mkdtemp(prefix=prefix, dir=base)


class Channel(threading.Thread):
    """
    One direction of a simulated radio channel, in real time.

    Reads transmit audio that one direwolf writes to a FIFO (through ALSA's
    file plugin) and feeds it to another direwolf's stdin at the real sample
    rate, with silence whenever nobody transmits, as a receiver would.  Without
    the silence the receiving demodulator would stop at the end of a
    transmission with carrier detect still on and never transmit itself.
    'taps' get a copy of everything (for decoding the traffic afterwards).
    """

    def __init__(self, fifo_path, rate, tap_path=None):
        super().__init__(daemon=True)
        if os.path.exists(fifo_path):
            os.remove(fifo_path)
        os.mkfifo(fifo_path, 0o666)
        os.chmod(fifo_path, 0o666)
        self.fd = os.open(fifo_path, os.O_RDONLY | os.O_NONBLOCK)
        self.rate = rate
        self.dest = None
        self.buf = bytearray()
        self.stop_flag = False
        self.tap = open(tap_path, "wb") if tap_path else None

    def connect(self, dest_stdin):
        self.dest = dest_stdin

    def run(self):
        t0 = time.time()
        sent = 0
        while not self.stop_flag:
            try:
                d = os.read(self.fd, 1 << 20)
                if d:
                    self.buf += d
            except BlockingIOError:
                pass
            want = int((time.time() - t0) * self.rate) - sent
            if want > 0:
                n = min(want * 2, len(self.buf)) // 2 * 2
                chunk = bytes(self.buf[:n]) + bytes(want * 2 - n)
                del self.buf[:n]
                sent += want
                if self.tap:
                    self.tap.write(chunk)
                try:
                    if self.dest:
                        self.dest.write(chunk)
                        self.dest.flush()
                except (BrokenPipeError, ValueError):
                    break
            time.sleep(0.01)

    def stop(self):
        self.stop_flag = True
        self.join(timeout=5)
        os.close(self.fd)
        if self.tap:
            self.tap.close()
