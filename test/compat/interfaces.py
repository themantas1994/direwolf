#!/usr/bin/env python3
"""
End to end interface tests: real direwolf processes of two or more builds,
no radio hardware.  Linux only (uses ALSA's file plugin, ptys, setpriv).

Receive audio goes in on stdin; transmit audio comes out through ALSA's
"file" plugin; client programs are simulated over KISS TCP, AGW, the KISS
pseudo terminal (-p) and a serial KISS port (a socat pty pair).

Checks (each against every reference build):

  rx-*       Same receive audio, default configuration: the frames given to
             KISS TCP and AGW (raw monitoring) clients are identical.
  rx-softfix SOFT_FIX 1 (opt in): the fork gives clients a superset, every
             extra frame is one marked as repaired on the console.
  tx-*       Frames from a client (KISS TCP / AGW / KISS pty / serial KISS)
             are transmitted; the transmit audio of every build is decoded by
             every build's atest and must give back exactly the frames sent.
  digi       APRS digipeater (DIGIPEAT, duplicate suppression) and connected
             mode digipeater (CDIGIPEAT): transmitted frames identical.
  igate      RF > APRS-IS lines sent to a local fake server identical; IS > RF
             gating of a message to a station heard on RF identical.
  beacon     PBEACON / CBEACON / OBJECT beacons: transmitted frames identical.
  config     Startup output for the sample configuration files is the same
             apart from version strings.
  connected  AX.25 connected mode session between two instances over a
             simulated channel, each build as caller and as answerer.
  stall      (only with --only stall; needs root) a client that stops reading
             must not stop frames reaching the other clients.

Usage:
  DW_RUN_AS=nobody interfaces.py --dut fork=BIN_DIR --ref upstream=BIN_DIR \
        [--ref ...] --work /tmp/dwrun/x [--only rx,tx,...] [--csv out.csv]

BIN_DIR must contain src/direwolf, src/atest and src/gen_packets readable by
the DW_RUN_AS user (direwolf delays 15 seconds when started as root).  The work
directory and /tmp/dwt are created if needed.
"""

import argparse
import csv
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ax25ref                      # noqa: E402
import dw_session as D              # noqa: E402
import interop                      # noqa: E402

TXDIR = "/tmp/dwt"                  # Short: direwolf truncates audio device names at 29 characters.
BATCH, BATCH_PAUSE = 20, 2.0        # Client sends this many frames, then waits.


# --------------------------------------------------------------------------

class Results:
    def __init__(self):
        self.rows = []
        self.failures = 0

    def add(self, test, detail, status, note=""):
        if status == "FAIL":
            self.failures += 1
        self.rows.append(dict(test=test, detail=detail, status=status, note=note))
        print("%-5s %-14s %-44s %s" % (status, test, detail, note), flush=True)


def exe(bindir, name):
    return os.path.join(bindir, "src", name)


def base_conf(rate=44100, modem="1200", tx=None, extra=()):
    kp, ap = D.free_port(), D.free_port()
    conf = ["ADEVICE stdin " + ("file:" + tx if tx else "null"),
            "ARATE %d" % rate, "CHANNEL 0", "MYCALL N0CALL-1", "MODEM " + modem,
            "KISSPORT %d" % kp, "AGWPORT %d" % ap] + list(extra)
    return conf, kp, ap


def txfile(tag):
    os.makedirs(TXDIR, exist_ok=True)
    try:
        os.chmod(TXDIR, 0o1777)
    except PermissionError:
        pass
    p = os.path.join(TXDIR, re.sub(r"[^a-z0-9]", "", tag.lower())[:12])
    if os.path.exists(p):
        os.remove(p)
    return p


def wait_tx_idle(path, quiet=2.0, timeout=120):
    """Wait until the transmit audio file has stopped growing."""
    t0 = time.time()
    last, since = -1, time.time()
    while time.time() - t0 < timeout:
        n = os.path.getsize(path) if os.path.exists(path) else 0
        if n != last:
            last, since = n, time.time()
        elif time.time() - since >= quiet and n > 0:
            return n
        time.sleep(0.2)
    return last


def decode_raw(bindirs, raw, rate, atest_args, workdir, tag):
    """Decode raw transmit audio with each build's atest.  Returns {name: frames}."""
    wav = os.path.join(workdir, tag + ".wav")
    ax25ref.raw_to_wav(raw, wav, rate)
    return {n: interop.decode(b, wav, atest_args) for n, b in bindirs.items()}


def frame(dest, src, digis=(), control=0x03, pid=0xF0, info=b"", c=(1, 1), h=0):
    """Any AX.25 frame.  c = (dest C bit, source C bit), h = number of used digipeaters."""
    def sp(t):
        a, s, _ = ax25ref.split_call(t)
        return a, s
    out = ax25ref.encode_address(*sp(dest), c[0], False)
    out += ax25ref.encode_address(*sp(src), c[1], len(digis) == 0)
    for i, d in enumerate(digis):
        out += ax25ref.encode_address(*sp(d), 1 if i < h else 0, i == len(digis) - 1)
    ctl = bytes(control) if isinstance(control, (list, tuple)) else bytes([control])
    return out + ctl + (bytes([pid]) if pid is not None else b"") + info


CMD, RSP, V1BOTH, V1NONE = (1, 0), (0, 1), (1, 1), (0, 0)

# AX.25 v2.2 frame types (control field values from the specification).
RAW_FRAMES = [
    ("SABM", frame("N0CALL-2", "W1ABC", (), 0x3F, None, b"", CMD)),
    ("SABME", frame("N0CALL-2", "W1ABC", (), 0x7F, None, b"", CMD)),
    ("UA", frame("W1ABC", "N0CALL-2", (), 0x73, None, b"", RSP)),
    ("DM", frame("W1ABC", "N0CALL-2", (), 0x1F, None, b"", RSP)),
    ("DISC", frame("N0CALL-2", "W1ABC", (), 0x53, None, b"", CMD)),
    ("FRMR", frame("W1ABC", "N0CALL-2", (), 0x97, None, bytes([0x10, 0x46, 0x01]), RSP)),
    # AX.25 v2.2 figure 4.6, with the parameter value erratum noted in Dire Wolf's xid.c.
    ("XID", frame("N0CALL-2", "W1ABC", (), 0xBF, None,
                  bytes([0x82, 0x80, 0x00, 0x17, 0x02, 0x02, 0x21, 0x00, 0x03, 0x03, 0x86, 0xA8, 0x02,
                         0x06, 0x02, 0x04, 0x00, 0x08, 0x01, 0x02, 0x09, 0x02, 0x10, 0x00, 0x0A, 0x01, 0x03]), CMD)),
    ("TEST", frame("N0CALL-2", "W1ABC", (), 0xF3, None, b"TEST frame payload", CMD)),
    ("I mod8", frame("N0CALL-2", "W1ABC", (), 0x6A, 0xF0, b"I frame N(R)=3 N(S)=5", CMD)),
    ("I mod8 P", frame("N0CALL-2", "W1ABC", (), 0x7E, 0xF0, b"\x7e\x7d\xc0\xdb flag escape bytes", CMD)),
    ("I mod128", frame("N0CALL-2", "W1ABC", (), [0xC8, 0x72], 0xF0, b"I frame modulo 128", CMD)),
    ("RR", frame("N0CALL-2", "W1ABC", (), 0x41, None, b"", RSP)),
    ("RNR", frame("N0CALL-2", "W1ABC", (), 0x45, None, b"", RSP)),
    ("REJ", frame("N0CALL-2", "W1ABC", (), 0x49, None, b"", RSP)),
    ("SREJ", frame("N0CALL-2", "W1ABC", (), 0x0D, None, b"", RSP)),
    ("RR mod128", frame("N0CALL-2", "W1ABC", (), [0x01, 0x14], None, b"", CMD)),
    ("UI NET/ROM", frame("NODES", "W1ABC", (), 0x03, 0xCF, bytes([0xFF]) + b"ALIAS " + bytes(range(0, 40)), V1BOTH)),
    ("UI IP", frame("QST", "W1ABC", (), 0x03, 0xCC, bytes([0x45, 0x00, 0x00, 0x1C]) + bytes(24), V1BOTH)),
    ("UI response", frame("APRS", "W1ABC", ("WIDE1-1",), 0x03, 0xF0, b">response UI", RSP)),
    ("UI v1 none", frame("APRS", "W1ABC", ("WIDE2-2",), 0x03, 0xF0, b">both C bits clear", V1NONE)),
    ("UI via used", frame("APRS", "W1ABC", ("R1", "R2", "R3"), 0x03, 0xF0, b">two of three used", V1BOTH, 2)),
    ("UI all bytes", frame("APRS", "W1ABC", (), 0x03, 0xF0, bytes(range(256)), V1BOTH)),
    ("UI 512", frame("APRS", "W1ABC", ("WIDE1-1",), 0x03, 0xF0, bytes((i * 37) & 0xFF for i in range(512)), V1BOTH)),
    ("UI no info", frame("APRS", "W1ABC", (), 0x03, 0xF0, b"", V1BOTH)),
]


def tx_frames(include_raw=True, il2p=False):
    frames = [ax25ref.tnc2_to_frame(l) for l in ax25ref.read_corpus(os.path.join(HERE, "aprs_ax25_corpus.txt"))]
    if include_raw:
        for name, f in RAW_FRAMES:
            if il2p and f[13] & 1 and ((f[6] >> 7) & 1) == ((f[13] >> 7) & 1) == 0:
                continue      # IL2P header type 1 can't express "both C bits clear".
            frames.append(f)
    return frames


def expected_after(frames, il2p):
    return [interop.il2p_expected(f) for f in frames] if il2p else frames


def compare_set(got, exp):
    """
    Frames found and unexpected extras, ignoring order: direwolf sends frames
    with a used digipeater in the path from its high priority queue first.
    """
    rest = list(got)
    found = 0
    for e in exp:
        if e in rest:
            rest.remove(e)
            found += 1
    return found, len(rest)


# --------------------------------------------------------------------------
# Receive: identical frames to clients
# --------------------------------------------------------------------------

def run_rx(bindir, name, conf_extra, raw, rate, modem, work):
    conf, kp, ap = base_conf(rate, modem, None, conf_extra)
    dw = D.Direwolf(exe(bindir, "direwolf"), conf, work, name)
    try:
        a = D.AgwClient(ap)
        a.send(0, "k")
        k = D.KissClient(kp)
        a.poll(2.0)            # Let the raw monitoring toggle take effect first.
        k.poll(0.2)
        feeder = threading.Thread(target=dw.feed, args=(raw + bytes(rate * 2),))
        feeder.start()
        while feeder.is_alive():
            k.poll(0.2)
            a.poll(0.2)
        k.drain_until_closed(120)
        a.drain_until_closed(10)
        dw.wait(30)
    finally:
        dw.stop()
    out = dw.output()
    kiss = [f[2] for f in k.frames if f[1] == 0]
    agw = [m[4][1:] for m in a.msgs if m[1] == "K"]
    repaired = len(re.findall(r"\[(SINGLE|DOUBLE|TRIPLE|TWO_SEP|THREE)", out))
    return kiss, agw, repaired, out


def t_rx(R, dut, refs, work):
    cases = [
        ("1200 A+ default", "1200", 44100, ["-n", "100"], []),
        ("1200 E+ (A+ alias)", "1200 E+", 44100, ["-n", "100"], []),
        ("1200 B", "1200 B", 44100, ["-n", "100"], []),
        ("1200 at 48k", "1200", 48000, ["-n", "100"], []),
        ("300", "300", 44100, ["-B", "300", "-n", "100"], []),
        ("2400 V26B (default)", "2400", 44100, ["-B", "2400", "-J", "-n", "100"], []),
        ("2400 V26A", "2400 V26A", 44100, ["-B", "2400", "-j", "-n", "100"], []),
        ("4800", "4800", 44100, ["-B", "4800", "-n", "100"], []),
        ("9600", "9600", 48000, ["-B", "9600", "-n", "100"], []),
        ("1200 FX.25", "1200", 44100, ["-X", "16", "-n", "100"], []),
        ("9600 IL2P", "9600", 48000, ["-B", "9600", "-I", "1", "-n", "100"], []),
    ]
    refname, refbin = next(iter(refs.items()))
    for title, modem, rate, gargs, extra in cases:
        wav = os.path.join(work, "rx_%s.wav" % re.sub(r"\W+", "_", title))
        interop.gen(refbin, gargs, rate, None, wav)
        raw, _ = D.wav_samples(wav)
        res = {}
        for n, b in [(dut[0], dut[1])] + list(refs.items()):
            res[n] = run_rx(b, n, extra, raw, rate, modem, os.path.join(work, "rx", re.sub(r"\W+", "_", title)))
        dk, da, drep, _ = res[dut[0]]
        R.add("rx-agw=kiss", title + " " + dut[0], "PASS" if da == dk else "FAIL",
              "agw %d kiss %d" % (len(da), len(dk)))
        for n in refs:
            rk, ra, _, _ = res[n]
            same = dk == rk and da == ra
            R.add("rx-kiss/agw", "%s: %s vs %s" % (title, dut[0], n), "PASS" if same else "FAIL",
                  "kiss %d/%d agw %d/%d repaired %d" % (len(dk), len(rk), len(da), len(ra), drep))

    # Opt in to soft repair: superset, extras are the ones marked repaired.
    wav = os.path.join(work, "rx_%s.wav" % re.sub(r"\W+", "_", cases[0][0]))
    raw, _ = D.wav_samples(wav)
    for lvl in (1, 2):
        dk, da, drep, out = run_rx(dut[1], dut[0], ["SOFT_FIX %d" % lvl], raw, 44100, "1200",
                                   os.path.join(work, "rx", "softfix%d" % lvl))
        rk, _, _, _ = run_rx(refbin, refname, [], raw, 44100, "1200", os.path.join(work, "rx", "softfix%d" % lvl))
        missing = [f for f in rk if f not in dk]
        extra = len(dk) - (len(rk) - len(missing))
        ok = not missing and extra == drep and da == dk
        R.add("rx-softfix", "SOFT_FIX %d, %s vs %s" % (lvl, dut[0], refname), "PASS" if ok else "FAIL",
              "kiss %d vs %d, extra %d, marked repaired %d, missing %d" % (len(dk), len(rk), extra, drep, len(missing)))


# --------------------------------------------------------------------------
# Transmit from clients
# --------------------------------------------------------------------------

TX_MODES = [
    # title, MODEM line, rate, extra config, atest args, il2p
    ("1200", "1200", 44100, [], ["-B", "1200"], False),
    ("300", "300", 44100, [], ["-B", "300"], False),
    ("2400", "2400", 44100, [], ["-B", "2400", "-J"], False),
    ("2400 V26A", "2400 V26A", 44100, [], ["-B", "2400", "-j"], False),
    ("4800", "4800", 44100, [], ["-B", "4800"], False),
    ("9600", "9600", 48000, [], ["-B", "9600"], False),
    ("1200 FX.25", "1200", 44100, ["FX25TX 16"], ["-B", "1200"], False),
    ("9600 FX.25", "9600", 48000, ["FX25TX 32"], ["-B", "9600"], False),
    ("1200 IL2P", "1200", 44100, ["IL2PTX 1"], ["-B", "1200"], True),
    ("9600 IL2P", "9600", 48000, ["IL2PTX 1"], ["-B", "9600"], True),
]


def run_tx(bindir, name, how, frames, modem, rate, extra, work):
    tx = txfile(name + how + modem.replace(" ", ""))
    conf, kp, ap = base_conf(rate, modem, tx, ["TXDELAY 20", "TXTAIL 5"] + extra)
    procs = []
    if how == "serial":
        a, b = os.path.join(TXDIR, "sa"), os.path.join(TXDIR, "sb")
        for p in (a, b):
            if os.path.lexists(p):
                os.remove(p)
        pre = []
        if os.environ.get("DW_RUN_AS") and os.geteuid() == 0:
            u = os.environ["DW_RUN_AS"]
            pre = ["setpriv", "--reuid=" + u, "--regid=" + u, "--clear-groups"]
        procs.append(subprocess.Popen(pre + ["socat", "pty,raw,echo=0,link=" + a, "pty,raw,echo=0,link=" + b],
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
        t0 = time.time()
        while not (os.path.exists(a) and os.path.exists(b)) and time.time() - t0 < 10:
            time.sleep(0.1)
        conf.append("SERIALKISS %s 9600" % a)
    args = ["-p"] if how == "pty" else []
    dw = D.Direwolf(exe(bindir, "direwolf"), conf, work, "%s_%s_%s" % (name, how, re.sub(r"\W+", "", modem + "".join(extra))), args)
    rx_back = []
    try:
        # Send in batches: direwolf discards frames when its transmit queue gets
        # too long (tq.c), as upstream does.
        if how == "kiss":
            k = D.KissClient(kp)
            for i, f in enumerate(frames):
                k.send(f)
                if i % BATCH == BATCH - 1:
                    k.poll(BATCH_PAUSE)
            k.poll(0.5)
        elif how == "agw":
            ag = D.AgwClient(ap)
            for i, f in enumerate(frames):
                ag.send(0, "K", data=bytes([0]) + f)
                if i % BATCH == BATCH - 1:
                    ag.poll(BATCH_PAUSE)
            ag.poll(0.5)
        elif how in ("pty", "serial"):
            path = "/tmp/kisstnc" if how == "pty" else os.path.join(TXDIR, "sb")
            t0 = time.time()
            while time.time() - t0 < 20 and not ("Virtual KISS TNC" in dw.output() or "Opened " in dw.output()
                                                 or os.path.exists(path) and how == "serial"):
                time.sleep(0.2)
            time.sleep(1.0)
            fd = os.open(path, os.O_RDWR | os.O_NOCTTY)
            for i, f in enumerate(frames):
                os.write(fd, ax25ref.kiss_encode(f))
                if i % BATCH == BATCH - 1:
                    time.sleep(BATCH_PAUSE)
            time.sleep(0.5)
            os.close(fd)
        # The ALSA null device takes audio faster than real time and direwolf then
        # sleeps for the rest of the air time, so the file alone can look idle.
        # xmit.c logs each frame it sends as "[0L] ..." or "[0H] ...".
        t0 = time.time()
        while time.time() - t0 < 900 and len(re.findall(r"^\[0[LH]\] ", dw.output(), re.M)) < len(frames):
            time.sleep(0.5)
        wait_tx_idle(tx, quiet=3.0)
    finally:
        dw.stop()
        for p in procs:
            p.terminate()
            p.wait()
    return tx, dw.output()


def t_tx(R, dut, refs, work, how_list=("kiss", "agw", "pty", "serial")):
    allbins = dict([dut] + list(refs.items()))
    for title, modem, rate, extra, aargs, il2p in TX_MODES:
        frames = tx_frames(True, il2p)
        exp = expected_after(frames, il2p)
        raws = {}
        for n, b in allbins.items():
            tx, _ = run_tx(b, n, "kiss", frames, modem, rate, extra, os.path.join(work, "tx"))
            dec = decode_raw(allbins, tx, rate, aargs, os.path.join(work, "tx"), "tx_%s_%s" % (n, re.sub(r"\W+", "_", title)))
            raws[n] = interop.md5(tx)
            for dn, got in dec.items():
                found, extra_n = compare_set(got, exp)
                status = "PASS" if found == len(exp) and extra_n == 0 else "FAIL"
                note = "%d/%d frames%s" % (found, len(exp), " extra %d" % extra_n if extra_n else "")
                if status == "FAIL" and "FX.25" in title and n != dut[0]:
                    # Upstream inverts the first bit after switching from the FX.25 sender
                    # back to the AX.25 one (separate NRZI states), so an AX.25 frame right
                    # after an FX.25 frame can lose its only opening flag.  Fixed in the fork.
                    status, note = "UPSTREAM-DEFECT", note + " (NRZI after FX.25, fixed in the fork)"
                R.add("tx-kiss", "%s: %s tx -> %s rx" % (title, n, dn), status, note)
        same = len(set(raws.values())) == 1
        if "FX.25" in title:
            # Expected to differ: the fork keeps NRZI continuous between HDLC flags and
            # the FX.25 block (gen_tone.c tone_gen_last_bit).  Decoding is checked above.
            R.add("tx-audio-md5", title, "INFO", ("identical" if same else "differs as expected (FX.25 NRZI fix) ") +
                  " ".join("%s:%s" % (k, v[:8]) for k, v in raws.items()))
        else:
            R.add("tx-audio-md5", title, "PASS" if same else "FAIL",
                  "identical transmit audio" if same else " ".join("%s:%s" % (k, v[:8]) for k, v in raws.items()))

    # Other client interfaces, 1200 bd only.
    frames = tx_frames(True)
    for how in how_list:
        if how == "kiss":
            continue
        if how == "serial" and shutil.which("socat") is None:
            R.add("tx-serial", "socat missing", "NOT TESTED")
            continue
        for n, b in allbins.items():
            tx, out = run_tx(b, n, how, frames, "1200", 44100, [], os.path.join(work, "tx"))
            dec = decode_raw({"upstream" if n == dut[0] else dut[0]: allbins[next(iter(refs)) if n == dut[0] else dut[0]]},
                             tx, 44100, ["-B", "1200"], os.path.join(work, "tx"), "tx_%s_%s" % (n, how))
            for dn, got in dec.items():
                found, extra_n = compare_set(got, frames)
                R.add("tx-" + how, "1200: %s tx -> %s rx" % (n, dn),
                      "PASS" if found == len(frames) and extra_n == 0 else "FAIL",
                      "%d/%d frames%s" % (found, len(frames), " extra %d" % extra_n if extra_n else ""))


def t_fallback(R, dut, refs, work):
    """
    Frames too long for the FEC layer are sent as plain AX.25.
      FX.25 (> ~239 bytes) right after an FX.25 frame: upstream loses it (NRZI
      level not shared between the senders; fixed in the fork).
      IL2P at 9600 bd (> 1023 bytes): sent without the G3RUH scrambler, so no
      9600 receiver decodes it.  Upstream defect, not fixed in the fork.
    """
    allbins = dict([dut] + list(refs.items()))
    small = ax25ref.tnc2_to_frame("N0CALL>APRS:>small")
    big = ax25ref.tnc2_to_frame("N0CALL>APRS:" + "0123456789ABCDEF" * 16 + "\n")
    huge = ax25ref.tnc2_to_frame("N0CALL>APRS:" + "0123456789abcdef" * 70)
    cases = [("FX.25 1200, AX.25 fallback after FX.25", [small, big], "1200", 44100, ["FX25TX 16"], False, True),
             ("FX.25 9600, AX.25 fallback after FX.25", [small, big, small, big], "9600", 48000, ["FX25TX 32"], False, True),
             ("IL2P 1200, AX.25 fallback", [small, huge], "1200", 44100, ["IL2PTX 1"], True, True),
             ("IL2P 9600, AX.25 fallback", [small, huge], "9600", 48000, ["IL2PTX 1"], True, False)]
    for title, frames, modem, rate, extra, il2p, fixed_in_fork in cases:
        exp = expected_after(frames, il2p)
        for n, b in allbins.items():
            tx, _ = run_tx(b, n, "kiss", frames, modem, rate, extra, os.path.join(work, "fallback"))
            got = decode_raw({n: b}, tx, rate, ["-B", modem], os.path.join(work, "fallback"),
                             "fb_%s_%s" % (n, re.sub(r"\W+", "_", title)))[n]
            found, extra_n = compare_set(got, exp)
            ok = found == len(exp)
            if ok:
                status = "PASS"
            elif n != dut[0] or not fixed_in_fork:
                status = "UPSTREAM-DEFECT"
            else:
                status = "FAIL"
            R.add("tx-fallback", "%s: %s" % (title, n), status, "%d/%d frames decoded" % (found, len(exp)))


# --------------------------------------------------------------------------
# Digipeaters, beacons
# --------------------------------------------------------------------------

DIGI_IN = [
    "W1ABC>APRS,WIDE1-1:>wide1",
    "W1ABC>APRS,WIDE2-2:>wide2",
    "W1ABC>APRS,WIDE1-1,WIDE2-1:>wide1 wide2",
    "W1ABC>APRS,N0CALL-1,WIDE2-1:>explicit",
    "W1ABC>APRS,WIDE3-3:>wide3 trapped",
    "W1ABC>APRS,WIDE7-7:>wide7 trapped",
    "W1ABC>APRS,TEST:>test alias",
    "W1ABC>APRS,DIGI1*,WIDE2-1:>after another digi",
    "W1ABC>APRS,WIDE1-1:>wide1",                 # duplicate, suppressed
    "W1ABC>APRS,WIDE2*:>exhausted",
    "W1ABC>APRS:>no path",
    "W1XYZ>APRS,WIDE1-1:!4237.14N/07120.83W-position",
]


def run_conf_tx(bindir, name, conf_extra, rx_raw, rate, work, tag, quiet=4.0, wait_first=0,
                wait_for=None, expect_rx=0, after_wait=0):
    """
    Start direwolf with extra configuration, optionally wait for wait_for (regex)
    in its output, feed receive audio, wait until expect_rx frames have been
    decoded and transmitting has stopped.  Returns the transmit audio file and log.
    """
    tx = txfile(tag + name)
    conf, kp, ap = base_conf(rate, "1200", tx, ["TXDELAY 20", "TXTAIL 5"] + conf_extra)
    dw = D.Direwolf(exe(bindir, "direwolf"), conf, work, name + "_" + tag)
    try:
        time.sleep(1.0 + wait_first)
        if wait_for:
            t0 = time.time()
            while time.time() - t0 < 60 and not re.search(wait_for, dw.output()):
                time.sleep(0.2)
        time.sleep(after_wait)
        if rx_raw:
            dw.feed(rx_raw + bytes(rate * 2), close=False)
        t0 = time.time()
        while time.time() - t0 < 120 and len(re.findall(r"^\[0[.\]]", dw.output(), re.M)) < expect_rx:
            time.sleep(0.2)
        # Done when neither the transmit audio nor the log has changed for 'quiet'
        # seconds.  (direwolf sleeps for the air time after writing the audio.)
        t0 = time.time()
        last, since = None, time.time()
        while time.time() - t0 < 180:
            now = (os.path.getsize(tx) if os.path.exists(tx) else 0, len(dw.output()))
            if now != last:
                last, since = now, time.time()
            elif time.time() - since >= quiet:
                break
            time.sleep(0.2)
    finally:
        dw.stop()
    return tx, dw.output()


def norm_tocall(frames):
    """Dire Wolf's own tocall has its version, APDW18 or APDW19.  Make them comparable."""
    out = []
    for f in frames:
        f = bytearray(f)
        if bytes(f[0:4]) == bytes(c << 1 for c in b"APDW"):
            f[4] = f[5] = ord("x") << 1
        out.append(bytes(f))
    return out


def t_digi(R, dut, refs, work):
    allbins = dict([dut] + list(refs.items()))
    lines = DIGI_IN + ["W1ABC>N0CALL-2,N0CALL-1:connected mode via us"]
    infile = os.path.join(work, "digi_in.txt")
    os.makedirs(work, exist_ok=True)
    with open(infile, "w") as f:
        f.write("\n".join(lines) + "\n")
    wav = os.path.join(work, "digi_in.wav")
    interop.gen(refs[next(iter(refs))], [], 44100, infile, wav)
    raw, _ = D.wav_samples(wav)
    # FULLDUP ON: received audio arrives here much faster than real time, and in half
    # duplex direwolf mutes the receiver while it transmits (demod_mute_input), so
    # frames "heard" during a digipeated transmission would be lost depending on timing.
    conf = ["FULLDUP ON", "DIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$ TRACE", "CDIGIPEAT 0 0"]
    got = {}
    for n, b in allbins.items():
        tx, out = run_conf_tx(b, n, conf, raw, 44100, os.path.join(work, "digi"), "digi", quiet=6.0,
                              expect_rx=len(lines))
        if not os.path.exists(tx):
            got[n] = []
            continue
        got[n] = decode_raw({n: b}, tx, 44100, ["-B", "1200"], os.path.join(work, "digi"), "digi_" + n)[n]
    for n in refs:
        same = got[dut[0]] == got[n]
        R.add("digi", "%s vs %s" % (dut[0], n), "PASS" if same and got[n] else "FAIL",
              "%d / %d frames digipeated" % (len(got[dut[0]]), len(got[n])))
    for f in got[dut[0]]:
        R.add("digi-frame", ax25ref.frame_to_tnc2(f)[:60], "INFO")


def t_beacon(R, dut, refs, work):
    allbins = dict([dut] + list(refs.items()))
    conf = ['PBEACON delay=0:01 every=10 sendto=0 lat=42^37.14N long=071^20.83W symbol="digi" overlay=S '
            'power=50 height=20 gain=4 comment="PBEACON test" via=WIDE1-1',
            'PBEACON delay=0:01 every=10 sendto=0 compress=1 lat=42^37.14N long=071^20.83W symbol=car '
            'altitude=1000 comment="compressed"',
            'CBEACON delay=0:01 every=10 sendto=0 info=">Custom beacon status"',
            'PBEACON delay=0:01 every=10 sendto=0 lat=42^37.14N long=071^20.83W symbol=/# '
            'freq=146.955 tone=74.4 offset=-0.600 comment="freq"',
            'PBEACON delay=0:01 every=10 sendto=0 ambiguity=2 lat=42^37.14N long=071^20.83W symbol="house"',
            ]
    got = {}
    for n, b in allbins.items():
        tx, out = run_conf_tx(b, n, conf, None, 44100, os.path.join(work, "beacon"), "bcn", quiet=3.0, wait_first=3)
        got[n] = decode_raw({n: b}, tx, 44100, ["-B", "1200"], os.path.join(work, "beacon"), "bcn_" + n)[n] \
            if os.path.exists(tx) else []
    def distinct(fr):
        out = []
        for f in norm_tocall(fr):
            if f not in out:
                out.append(f)
        return out
    for n in refs:
        a, b = distinct(got[dut[0]]), distinct(got[n])
        R.add("beacon", "%s vs %s" % (dut[0], n), "PASS" if a == b and a else "FAIL",
              "%d / %d distinct beacons (tocall version ignored)" % (len(a), len(b)))
    for f in distinct(got[dut[0]]):
        R.add("beacon-frame", ax25ref.frame_to_tnc2(f)[:70], "INFO")


# --------------------------------------------------------------------------
# IGate against a local fake APRS-IS server
# --------------------------------------------------------------------------

class FakeAprsIs(threading.Thread):
    def __init__(self, to_rf_lines=()):
        super().__init__(daemon=True)
        self.port = D.free_port()
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", self.port))
        self.srv.listen(1)
        self.lines = []
        self.to_rf = list(to_rf_lines)
        self.stop_flag = False

    def run(self):
        self.srv.settimeout(1.0)
        while not self.stop_flag:
            try:
                c, _ = self.srv.accept()
                break
            except socket.timeout:
                continue
        else:
            return
        c.settimeout(0.2)
        c.sendall(b"# aprsc 2.1.14 test\r\n")
        buf = b""
        sent_rf = False
        t_login = None
        while not self.stop_flag:
            try:
                d = c.recv(4096)
                if not d:
                    break
                buf += d
            except socket.timeout:
                pass
            while b"\n" in buf:
                line, buf = buf.split(b"\n", 1)
                line = line.rstrip(b"\r")
                self.lines.append(line)
                if line.startswith(b"user "):
                    c.sendall(b"# logresp N0CALL-1 verified, server TEST\r\n")
                    t_login = time.time()
            # Send the IS > RF traffic once the station it is for has been heard on RF
            # (its packet uploaded), as the IGate only gates messages to stations heard.
            if t_login and not sent_rf and any(l.startswith(b"W1XYZ>") for l in self.lines):
                time.sleep(1.0)
                for l in self.to_rf:
                    c.sendall(l + b"\r\n")
                sent_rf = True
        c.close()

    def stop(self):
        self.stop_flag = True
        self.join(timeout=5)
        self.srv.close()


def t_igate(R, dut, refs, work):
    allbins = dict([dut] + list(refs.items()))
    lines = ax25ref.read_corpus(os.path.join(HERE, "aprs_ax25_corpus.txt"))[:40] + [
        "W1XYZ>APRS,WIDE1-1:!4237.14N/07120.83W-heard on RF",
        "W1ABC>APRS,TCPIP*:>should not be gated (TCPIP)",
        "W1ABC>APRS,NOGATE:>should not be gated (NOGATE)",
        "W1ABC>APRS,RFONLY:>should not be gated (RFONLY)",
        "W1ABC>APRS:}W2ABC>APRS,TCPIP,W1ABC*:>third party from IS",
    ]
    os.makedirs(work, exist_ok=True)
    infile = os.path.join(work, "ig_in.txt")
    with open(infile, "w") as f:
        f.write("\n".join(lines) + "\n")
    wav = os.path.join(work, "ig_in.wav")
    interop.gen(refs[next(iter(refs))], [], 44100, infile, wav)
    raw, _ = D.wav_samples(wav)
    to_rf = [b"K1ABC>APRS,TCPIP*,qAC,T2TEST::W1XYZ    :Message to RF station{01",
             b"K1ABC>APRS,TCPIP*,qAC,T2TEST::NOTHEARD :Not heard, not gated{02"]
    up, rf = {}, {}
    for n, b in allbins.items():
        srv = FakeAprsIs(to_rf)
        srv.start()
        conf = ["FULLDUP ON", "IGSERVER 127.0.0.1:%d" % srv.port, "IGLOGIN N0CALL-1 13023", "IGTXVIA 0 WIDE1-1",
                "IGTXLIMIT 6 10"]
        tx, out = run_conf_tx(b, n, conf, raw, 44100, os.path.join(work, "igate"), "ig", quiet=10.0,
                              wait_for=r"logresp", expect_rx=len(lines), after_wait=11)
        srv.stop()
        up[n] = [l for l in srv.lines if not l.startswith(b"user ") and not l.startswith(b"#")]
        login = [l for l in srv.lines if l.startswith(b"user ")]
        rf[n] = decode_raw({n: b}, tx, 44100, ["-B", "1200"], os.path.join(work, "igate"), "ig_" + n)[n] \
            if os.path.exists(tx) else []
        R.add("igate-login", n, "INFO", login[0].decode(errors="replace")[:80] if login else "no login")
    for n in refs:
        R.add("igate-rf>is", "%s vs %s" % (dut[0], n), "PASS" if up[dut[0]] == up[n] and up[n] else "FAIL",
              "%d / %d lines uploaded" % (len(up[dut[0]]), len(up[n])))
        R.add("igate-is>rf", "%s vs %s" % (dut[0], n),
              "PASS" if norm_tocall(rf[dut[0]]) == norm_tocall(rf[n]) and rf[n] else "FAIL",
              "%d / %d frames transmitted" % (len(rf[dut[0]]), len(rf[n])))
    for l in up[dut[0]][-6:]:
        R.add("igate-line", l.decode(errors="replace")[:70], "INFO")
    for f in rf[dut[0]]:
        R.add("igate-rf-frame", ax25ref.frame_to_tnc2(f)[:70], "INFO")


# --------------------------------------------------------------------------
# Configuration files
# --------------------------------------------------------------------------

def t_config(R, dut, refs, work, conf_dir):
    """Start each build with each sample config (audio replaced by stdin/null), compare messages."""
    samples = []
    for fn in sorted(os.listdir(conf_dir)):
        if fn.endswith(".conf"):
            samples.append(os.path.join(conf_dir, fn))
    norm = re.compile(r"(Dire Wolf.*(Release|Version|DEVELOPMENT).*|.*port \d+.*|.*Avahi.*|"
                      r".*root user.*|.*privileges.*|.*security risk.*|Reading config file .*|"
                      r"Includes optional support for:.*|Bind failed.*|Address already in use.*|"
                      r"Some other application is probably already using port.*|"
                      r"Try using a different port number with KISSPORT.*)")
    for path in samples:
        text = open(path, encoding="latin-1").read()
        lines = [l for l in text.splitlines() if not re.match(r"\s*(ADEVICE|ARATE|KISSPORT|AGWPORT|PTT|"
                                                             r"GPSD|GPSNMEA|IGSERVER|DCD|TXINH|PBEACON|"
                                                             r"OBEACON|TBEACON|CBEACON|IBEACON)\b", l, re.I)]
        outs = {}
        for n, b in [dut] + list(refs.items()):
            conf, kp, ap = base_conf(44100, "1200", None, [])
            conf = conf[:2] + lines + conf[5:]
            dw = D.Direwolf(exe(b, "direwolf"), conf, os.path.join(work, "config"), n + "_cfg")
            time.sleep(3)
            dw.stop()
            outs[n] = [l for l in dw.output().splitlines() if l.strip() and not norm.match(l)]
        for n in refs:
            same = outs[dut[0]] == outs[n]
            diff = [l for l in outs[dut[0]] if l not in outs[n]] + ["-" + l for l in outs[n] if l not in outs[dut[0]]]
            R.add("config", "%s: %s vs %s" % (os.path.basename(path), dut[0], n), "PASS" if same else "FAIL",
                  "%d lines" % len(outs[n]) if same else "differs: %s" % diff[:3])


# --------------------------------------------------------------------------
# Connected mode between two instances
# --------------------------------------------------------------------------

def t_connected(R, dut, refs, work, rate=44100):
    """
    Station A (N0CALL-1) calls station B (N0CALL-2) over a simulated channel,
    both through AGW clients.  Data both ways, including all 256 byte values
    and a block longer than PACLEN, then a disconnect.
    """
    pairs = []
    for n in refs:
        pairs += [(dut, (n, refs[n])), ((n, refs[n]), dut)]
    payload_a = [b"Hello from A " + bytes([i]) * 10 for i in range(5)] + [bytes(range(256)), bytes(range(256)) * 4]
    payload_b = [b"Reply from B", bytes(range(255, -1, -1)), b"x" * 700]
    for (an, ab), (bn, bb) in pairs:
        title = "%s calls %s" % (an, bn)
        cw = os.path.join(work, "conn", re.sub(r"\W+", "_", title))
        os.makedirs(cw, exist_ok=True)
        f_ab, f_ba = os.path.join(TXDIR, "ab"), os.path.join(TXDIR, "ba")
        ch_ab = D.Channel(f_ab, rate, os.path.join(cw, "ab.raw"))
        ch_ba = D.Channel(f_ba, rate, os.path.join(cw, "ba.raw"))
        ca, _, apa = base_conf(rate, "1200", f_ab, ["TXDELAY 20", "TXTAIL 5", "FRACK 4", "PACLEN 128"])
        cb, _, apb = base_conf(rate, "1200", f_ba, ["TXDELAY 20", "TXTAIL 5", "FRACK 4", "PACLEN 128"])
        cb = [l.replace("MYCALL N0CALL-1", "MYCALL N0CALL-2") for l in cb]
        A = D.Direwolf(exe(ab, "direwolf"), ca, cw, "A_" + an)
        B = D.Direwolf(exe(bb, "direwolf"), cb, cw, "B_" + bn)
        ch_ab.connect(B.p.stdin)
        ch_ba.connect(A.p.stdin)
        ch_ab.start()
        ch_ba.start()
        recv_a, recv_b = b"", b""
        status = []
        try:
            ga, gb = D.AgwClient(apa), D.AgwClient(apb)
            ga.send(0, "X", b"N0CALL-1")
            gb.send(0, "X", b"N0CALL-2")
            ga.poll(1)
            gb.poll(1)
            ga.send(0, "C", b"N0CALL-1", b"N0CALL-2")
            t0 = time.time()
            conn_a = conn_b = False
            while time.time() - t0 < 30 and not (conn_a and conn_b):
                ga.poll(0.2)
                gb.poll(0.2)
                conn_a = any(m[1] == "C" for m in ga.msgs)
                conn_b = any(m[1] == "C" for m in gb.msgs)
            status.append("connected A=%s B=%s" % (conn_a, conn_b))
            for p in payload_a:
                ga.send(0, "D", b"N0CALL-1", b"N0CALL-2", p)
            for p in payload_b:
                gb.send(0, "D", b"N0CALL-2", b"N0CALL-1", p)
            want_a, want_b = b"".join(payload_b), b"".join(payload_a)
            t0 = time.time()
            while time.time() - t0 < 90:
                ga.poll(0.2)
                gb.poll(0.2)
                recv_a = b"".join(m[4] for m in ga.msgs if m[1] == "D")
                recv_b = b"".join(m[4] for m in gb.msgs if m[1] == "D")
                if len(recv_a) >= len(want_a) and len(recv_b) >= len(want_b):
                    break
            ga.send(0, "d", b"N0CALL-1", b"N0CALL-2")
            t0 = time.time()
            disc = False
            while time.time() - t0 < 20 and not disc:
                ga.poll(0.2)
                gb.poll(0.2)
                disc = any(m[1] == "d" for m in gb.msgs) and any(m[1] == "d" for m in ga.msgs)
            status.append("disconnected=%s" % disc)
            ok = conn_a and conn_b and recv_a == want_a and recv_b == want_b and disc
            R.add("connected", title, "PASS" if ok else "FAIL",
                  "%s; A got %d/%d B got %d/%d bytes" % ("; ".join(status), len(recv_a), len(want_a),
                                                       len(recv_b), len(want_b)))
        finally:
            A.stop()
            B.stop()
            ch_ab.stop()
            ch_ba.stop()
        # All frames on the channel, decoded by the other side's atest, must have good FCS (they do by
        # definition) and the session must not have needed retransmissions caused by decoding problems.
        for tag, rawp, rx in (("A>B", os.path.join(cw, "ab.raw"), bb), ("B>A", os.path.join(cw, "ba.raw"), ab)):
            fr = decode_raw({"rx": rx}, rawp, rate, ["-B", "1200"], cw, tag.replace(">", "to"))["rx"]
            R.add("connected-frames", "%s %s" % (title, tag), "INFO", "%d frames on air" % len(fr))


# --------------------------------------------------------------------------
# A client that stops reading
# --------------------------------------------------------------------------

def t_stall(R, dut, refs, work, nframes=4000):
    """
    One client (AGW with raw monitoring, or KISS TCP) connects and never reads.
    Another, healthy, client counts the frames it gets while 9600 bd audio with
    nframes frames is decoded.  Upstream 1.8.2 sends to clients with blocking
    send() calls from the receive thread, so once the stalled client's socket
    buffers are full everything stops.  The kernel would buffer up to 4 MB, so
    tcp_wmem is lowered for the test (root only; restored afterwards).
    """
    path = "/proc/sys/net/ipv4/tcp_wmem"
    try:
        orig = open(path).read()
        open(path, "w").write("4096 8192 16384")
    except OSError:
        R.add("stall", "needs root to lower tcp_wmem", "NOT TESTED")
        return
    try:
        os.makedirs(work, exist_ok=True)
        infile = os.path.join(work, "stall_in.txt")
        with open(infile, "w") as f:
            for i in range(nframes):
                f.write("W1ABC-%d>APRS,WIDE1-1:>Stall test frame %05d %s\n" % (i % 16, i, "x" * 150))
        wav = os.path.join(work, "stall_in.wav")
        interop.gen(refs[next(iter(refs))], ["-B", "9600"], 48000, infile, wav)
        raw, rate = D.wav_samples(wav)
        for n, b in [dut] + list(refs.items()):
            for staller in ("agw", "kiss"):
                conf, kp, ap = base_conf(48000, "9600", None, [])
                dw = D.Direwolf(exe(b, "direwolf"), conf, os.path.join(work, "stall"), "%s_%s" % (n, staller))
                try:
                    port = ap if staller == "agw" else kp
                    D.wait_port(port).close()
                    st = socket.socket()
                    st.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 2048)
                    st.connect(("127.0.0.1", port))
                    if staller == "agw":
                        st.sendall(D.AgwClient.HDR.pack(0, ord("k"), b"", b"", 0, 0))
                        victim = D.KissClient(kp)
                    else:
                        victim = D.AgwClient(ap)
                        victim.send(0, "k")
                    victim.poll(2)
                    threading.Thread(target=dw.feed, args=(raw, 65536, False), daemon=True).start()
                    for _ in range(12):
                        victim.poll(5)
                    got = len(victim.frames) if staller == "agw" else sum(1 for m in victim.msgs if m[1] == "K")
                    st.close()
                finally:
                    dw.stop()
                R.add("stall", "%s, stalled %s client" % (n, staller.upper()), "PASS" if got == nframes else "FAIL",
                      "healthy client got %d/%d frames" % (got, nframes))
    finally:
        open(path, "w").write(orig)


# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dut", required=True, help="NAME=BIN_DIR of the build under test")
    ap.add_argument("--ref", action="append", required=True, help="NAME=BIN_DIR of a reference build")
    ap.add_argument("--work", required=True)
    ap.add_argument("--only", default="rx,tx,digi,beacon,igate,config,connected")
    ap.add_argument("--conf-dir", default=None, help="directory with sample .conf files (build/conf)")
    ap.add_argument("--csv")
    a = ap.parse_args()
    dut = tuple(a.dut.split("=", 1))
    refs = dict(r.split("=", 1) for r in a.ref)
    os.makedirs(a.work, exist_ok=True)
    try:
        os.chmod(a.work, 0o1777)
    except PermissionError:
        pass
    only = set(a.only.split(","))
    R = Results()
    if "rx" in only:
        t_rx(R, dut, refs, a.work)
    if "tx" in only:
        t_tx(R, dut, refs, a.work)
        t_fallback(R, dut, refs, a.work)
    if "digi" in only:
        t_digi(R, dut, refs, a.work)
    if "beacon" in only:
        t_beacon(R, dut, refs, a.work)
    if "igate" in only:
        t_igate(R, dut, refs, a.work)
    if "config" in only and a.conf_dir:
        t_config(R, dut, refs, a.work, a.conf_dir)
    if "connected" in only:
        t_connected(R, dut, refs, a.work)
    if "stall" in only:
        t_stall(R, dut, refs, a.work)
    if a.csv:
        with open(a.csv, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=["test", "detail", "status", "note"], lineterminator="\n")
            w.writeheader()
            w.writerows(R.rows)
    print("\n%d rows, %d failed" % (len(R.rows), R.failures))
    return 1 if R.failures else 0


if __name__ == "__main__":
    sys.exit(main())
