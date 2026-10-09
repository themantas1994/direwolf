#!/usr/bin/env python3
"""
CPU time and peak memory of atest builds on identical audio.

Each case's WAV file is made once by --gen-packets; every atest decodes it
--runs times, each run a fresh process.  User + system CPU seconds and the
maximum resident set size come from wait4().  Reported: median, min and max
CPU, max RSS, the number of packets decoded and the audio length, so the real
time factor is audio_s / cpu_s.  Run alone on the machine.

Usage:
  cpu_mem.py --atest upstream=PATH --atest fork=PATH [--atest 'fork -S1=PATH:-S1'] \
             --gen-packets PATH --work DIR --out cpu_mem.csv
"""

import argparse
import csv
import os
import statistics
import subprocess

CASES = [  # name, gen_packets args, atest args, sample rate
    ("1200 A+ 44.1k", ["-n", "1000"], [], 44100),
    ("1200 A+ 48k", ["-n", "1000"], [], 48000),
    ("1200 A+ 96k", ["-n", "1000"], [], 96000),
    ("1200 B 44.1k", ["-n", "1000"], ["-P", "B"], 44100),
    ("300 44.1k", ["-B", "300", "-n", "300"], ["-B", "300"], 44100),
    ("9600 48k", ["-B", "9600", "-n", "1000"], ["-B", "9600"], 48000),
    ("9600 96k", ["-B", "9600", "-n", "1000"], ["-B", "9600"], 96000),
]


def run(cmd):
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = p.stdout.read().decode("latin-1")
    _, status, ru = os.wait4(p.pid, 0)
    dec = [l for l in out.splitlines() if "packets decoded in" in l]
    return ru.ru_utime + ru.ru_stime, ru.ru_maxrss, (dec[-1].split()[0] if dec else "?"), status


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--atest", action="append", required=True,
                    help="LABEL=PATH[:EXTRA ARGS], e.g. fork=build/src/atest or 'fork -S1=build/src/atest:-S1'")
    ap.add_argument("--gen-packets", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--runs", type=int, default=5)
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    builds = []
    for spec in a.atest:
        label, rest = spec.split("=", 1)
        path, _, extra = rest.partition(":")
        builds.append((label, path, extra.split()))

    rows = []
    for name, gargs, aargs, rate in CASES:
        wav = os.path.join(a.work, name.replace(" ", "_").replace(".", "") + ".wav")
        if not os.path.exists(wav):
            subprocess.run([a.gen_packets] + gargs + ["-r", str(rate), "-o", wav], stdout=subprocess.DEVNULL, check=True)
        for label, path, extra in builds:
            cpu, rss, dec = [], [], None
            for _ in range(a.runs):
                c, m, d, st = run([path] + aargs + extra + [wav])
                if st != 0:
                    raise SystemExit("%s failed on %s (status %d)" % (label, name, st))
                cpu.append(c)
                rss.append(m)
                dec = d
            row = dict(case=name, build=label, audio_s=round((os.path.getsize(wav) - 44) / 2 / rate, 1), decoded=dec,
                       cpu_s_median=round(statistics.median(cpu), 3), cpu_s_min=round(min(cpu), 3),
                       cpu_s_max=round(max(cpu), 3), maxrss_kb=max(rss))
            rows.append(row)
            print(row, flush=True)

    with open(a.out, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()), lineterminator="\n")
        w.writeheader()
        w.writerows(rows)


if __name__ == "__main__":
    main()
