#!/usr/bin/env python3
"""
Audio over UDP (ADEVICE udp:PORT, as fed by SDR software) and decode latency,
with real direwolf processes.

1. The WAV file is sent to each build in real time as 1024 byte datagrams; the
   KISS frames must be the same as when the same audio is given on stdin, and
   the same for every build.
2. Latency: one frame at a time (1200 and 9600 bd, 48 kHz) between 0.5 s and
   1 s of silence, sent in real time; the time from the last audio sample of
   the transmission (frame and trailing flags) leaving this script to the
   KISS frame arriving, 20 times.  Negative values: the frame was delivered
   while its trailing flags were still being received.

Run alone on the machine: UDP is not flow controlled, so a busy CPU loses
datagrams and timing.

Usage:
  DW_RUN_AS=nobody udp_audio.py --build fork=BIN_DIR --build upstream=BIN_DIR \
        --work /tmp/dwrun/udp WAV [--pace 1.0]
"""

import argparse
import os
import select
import socket
import statistics
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import dw_session as D              # noqa: E402
import interfaces as I              # noqa: E402

CHUNK = 1024


def udp_conf(port, rate, modem):
    kp, ap = D.free_port(), D.free_port()
    return ["ADEVICE udp:%d null" % port, "ARATE %d" % rate, "CHANNEL 0", "MYCALL N0CALL-1",
            "MODEM " + modem, "KISSPORT %d" % kp, "AGWPORT %d" % ap], kp


def send_paced(s, port, buf, rate, pace):
    t0 = time.time()
    for i in range(0, len(buf), CHUNK):
        s.sendto(buf[i:i + CHUNK], ("127.0.0.1", port))
        target = t0 + (i + CHUNK) / 2 / rate / pace
        while time.time() < target:
            pass


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--build", action="append", required=True, help="NAME=BIN_DIR (src/direwolf, src/gen_packets)")
    ap.add_argument("--work", required=True)
    ap.add_argument("--pace", type=float, default=1.0, help="speed relative to real time")
    ap.add_argument("wav", help="16 bit mono WAV, 1200 bd")
    a = ap.parse_args()
    builds = [tuple(b.split("=", 1)) for b in a.build]
    os.makedirs(a.work, exist_ok=True)
    try:
        os.chmod(a.work, 0o1777)
    except PermissionError:
        pass

    raw, rate = D.wav_samples(a.wav)
    res = {}
    for name, bindir in builds:
        stdin_kiss, _, _, _ = I.run_rx(bindir, name, [], raw, rate, "1200", os.path.join(a.work, "stdin_" + name))
        port = D.free_port()
        conf, kp = udp_conf(port, rate, "1200")
        dw = D.Direwolf(I.exe(bindir, "direwolf"), conf, os.path.join(a.work, "udp_" + name), name)
        k = D.KissClient(kp)
        k.poll(1)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        send_paced(s, port, raw, rate, a.pace)
        send_paced(s, port, bytes(rate * 2), rate, a.pace)          # one second of silence
        k.poll(3)                                                   # frames waited in the socket
        dw.stop()
        res[name] = [f[2] for f in k.frames if f[1] == 0]
        print("%s: KISS frames, audio via UDP at %.1fx real time: %d; via stdin: %d; identical: %s"
              % (name, a.pace, len(res[name]), len(stdin_kiss), res[name] == stdin_kiss))
    names = [n for n, _ in builds]
    print("UDP, all builds identical:", all(res[n] == res[names[0]] for n in names))

    one = os.path.join(a.work, "one.wav")
    r = 48000
    for mode, margs in (("1200", []), ("9600", ["-B", "9600"])):
        subprocess.run([I.exe(builds[-1][1], "gen_packets"), "-r", str(r), "-o", one] + margs + ["-"],
                       input=b"N0CALL>APRS,WIDE1-1:!4237.14N/07120.83W-latency test\n",
                       stdout=subprocess.DEVNULL, check=True)
        fr, _ = D.wav_samples(one)
        for name, bindir in builds:
            port = D.free_port()
            conf, kp = udp_conf(port, r, mode)
            dw = D.Direwolf(I.exe(bindir, "direwolf"), conf, os.path.join(a.work, "lat_%s_%s" % (mode, name)), name)
            ks = D.wait_port(kp)
            ks.setblocking(False)              # KissClient.poll would block in recv for up to 0.2 s
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            lat = []
            for _ in range(20):
                # 0.5 s of silence, the transmission, 1 s of silence, in real time; the
                # KISS socket is watched all along.
                buf = bytes(r) + fr + bytes(2 * r)
                t0 = time.time()
                t_end = t0 + (r + len(fr)) / 2 / r
                got = None
                for i in range(0, len(buf), CHUNK):
                    s.sendto(buf[i:i + CHUNK], ("127.0.0.1", port))
                    while time.time() < t0 + (i + CHUNK) / 2 / r:
                        rd, _, _ = select.select([ks], [], [], 0.0005)
                        if rd and ks.recv(65536) and got is None:
                            got = time.time() - t_end
                if got is not None:
                    lat.append(got * 1000)
            dw.stop()
            if lat:
                print("latency %s bd %s: %d/20 decoded, KISS frame received %.1f ms (median; min %.1f, max %.1f) "
                      "after the last audio sample of the transmission (negative: during the trailing flags)"
                      % (mode, name, len(lat), statistics.median(lat), min(lat), max(lat)))
            else:
                print("latency %s bd %s: nothing decoded" % (mode, name))


if __name__ == "__main__":
    main()
