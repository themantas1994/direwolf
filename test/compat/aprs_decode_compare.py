#!/usr/bin/env python3
"""
Compare how different builds decode (interpret) APRS packets, and check
positions against an independent parser.

  * Input: the corpus, every truncation of each corpus packet's information
    field, single character substitutions, and the regression packets below.
  * Each build's decode_aprs is run on the same input.  Output is split per
    packet and compared.  A crash (signal) is detected per packet.
  * Differences are put into known categories (fixes made in the fork) or
    reported as unexplained.
  * If aprslib (an independent Python APRS parser) is installed, positions
    that both parse are compared: they must agree within 0.0001 degree.

Usage:
  aprs_decode_compare.py --build fork=BUILD --build upstream=BUILD [...]
                         --work DIR [--max N] [--csv out.csv]
"""

import argparse
import csv
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ax25ref  # noqa: E402

_ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

# Packets for defects fixed in the fork (upstream 1.8.2 and dev crash or misbehave).
REGRESSION = [
    "N0CALL>APRS:{DA!AIVDM,1,1,,A,13u?etPv2;0n:dDPwUM1U1Cb069D,0",     # AIS, fill bits field missing
    "N0CALL>APRS:{DA",                                                  # AIS, empty sentence
    "N0CALL>APRS:{DA!AIVDM,1,1,,A,,0*26",                               # AIS, empty payload
    "N1ZKO-7>T2TS7X:`c6wl!i[/>`",                                       # MIC-E, comment just ` (device id)
    "N1ZKO-7>T2TS7X:`c6wl!i[/>'",                                       # MIC-E, comment just '
    "N1ZKO-7>T2TS7X:`c6wl!i[/>`_",                                      # MIC-E, 2 character comment
    "N0CALL>APRS:!4903.50N/07201.75W#PHG5~32",                          # PHG height code not a digit
    "N0CALL>APRS:!4903.50N/07201.75W#PHG5!32",
    "N0CALL>APRS:!4903.50N/07201.75W#DFS2Z60",
    "N0CALL>APRS:!4903.50N/07201.75W-Battery 100%",                     # no false CTCSS warning
    "N0CALL>APRS:?APRS? 34.02,-117.15,0200",                            # general query footprint
    "N0CALL>APRS:@092345z4903.50N/07201.75W_<0x00>90/000g000t066r000p000",  # wind direction: sscanf EOF
    "N0CALL>APRS:!4903.50N/07201.75W>088/   ",                          # speed: sscanf EOF
    "N0CALL>APRS:!4903.50N/07201.75W#RNG    ",                          # range: sscanf EOF
]

# Known, intended differences between the fork and upstream 1.8.2.  Each
# normalizer is applied to both outputs; if they are then equal, the
# difference is explained by that fix.
EXPLAINED = [
    ("upstream debug output removed (#656)",
     lambda t: re.sub(r"^DEBUG: General Query.*\n?", "", t, flags=re.M)),
    ("'100%' is not a CTCSS tone (#657)",
     lambda t: re.sub(r"(, PL \d+(\.\d+)?|^.*looks like it might be a CTCSS tone.*\n|^For most systems to recognize it.*\n)",
                      "", t, flags=re.M)),
    ("invalid PHG/DFS height code: shown without height, not a garbage value",
     lambda t: re.sub(r" height\(HAAT\)=-?\d+ft=-?\d+m", "", t)),
    ("sscanf EOF: uninitialized course/speed/range no longer shown",
     lambda t: re.sub(r"(, direction -?\d+|, -?\d+ km/h \(-?\d+ MPH\)|, range=?[-\d.]+|, course -?\d+|"
                      r"wind -?[\d.]+ mph(, direction -?\d+)?)", "", t)),
]


def make_inputs(corpus, max_n):
    out = []
    seen = set()

    def add(s):
        if s not in seen and ":" in s:
            seen.add(s)
            out.append(s)
    for s in REGRESSION:
        add(s)
    for line in corpus:
        add(line)
    for line in corpus:
        head, info = line.split(":", 1)
        for k in range(len(info)):
            add(head + ":" + info[:k])
    subs = ["", "!", "/", "\\", "`", "'", "{", "}", ":", ";", "0", "9", "z", "Z", "_", "~", " ", ".", "-", "<0x00>", "<0xff>"]
    for line in corpus:
        head, info = line.split(":", 1)
        if "<0x" in info:
            continue
        for k in range(min(len(info), 40)):
            for c in subs:
                add(head + ":" + info[:k] + c + info[k + 1:])
                if len(out) >= max_n:
                    return out
    return out


def run_decode(exe, workdir, lines):
    """Returns ({index: output text}, set of crashed indexes)."""
    res, crashed = {}, set()

    def go(chunk):
        path = os.path.join(workdir, "in.txt")
        with open(path, "w", encoding="latin-1") as f:
            for i, s in chunk:
                f.write("# ---- %d\n%s\n" % (i, s))
            f.write("# ---- end\n")
        p = subprocess.run([exe, path], cwd=workdir, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        return p.returncode, _ANSI.sub("", p.stdout.decode("latin-1"))

    def parse(text):
        parts = re.split(r"^# ---- (\d+|end)\n", text, flags=re.M)
        for j in range(1, len(parts) - 1, 2):
            if parts[j] != "end":
                body = [l for l in parts[j + 1].splitlines()
                        if l and "symbols-new.txt" not in l and "OVERLAID" not in l]
                res[int(parts[j])] = "\n".join(body)

    items = list(enumerate(lines))
    for c in range(0, len(items), 200):
        chunk = items[c:c + 200]
        rc, text = go(chunk)
        if rc == 0:
            parse(text)
        else:
            for it in chunk:           # Find which packet(s) crash.
                rc1, t1 = go([it])
                if rc1 == 0:
                    parse(t1)
                else:
                    crashed.add(it[0])
                    res[it[0]] = "*** CRASH (exit status %d) ***" % rc1
    return res, crashed


def dw_position(text):
    m = re.search(r"([NS]) (\d+) (\d+\.\d+), ([EW]) (\d+) (\d+\.\d+)", text)
    if not m:
        return None
    lat = int(m.group(2)) + float(m.group(3)) / 60
    lon = int(m.group(5)) + float(m.group(6)) / 60
    return (-lat if m.group(1) == "S" else lat, -lon if m.group(4) == "W" else lon)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--build", action="append", required=True, help="NAME=BUILD_DIR (first is the build under test)")
    ap.add_argument("--work", required=True)
    ap.add_argument("--corpus", default=os.path.join(HERE, "aprs_ax25_corpus.txt"))
    ap.add_argument("--max", type=int, default=12000)
    ap.add_argument("--csv")
    ap.add_argument("--valgrind", action="store_true",
                    help="also run the first build's decode_aprs under valgrind: no errors allowed")
    a = ap.parse_args()
    builds = [b.split("=", 1) for b in a.build]
    os.makedirs(a.work, exist_ok=True)
    for fn in ("tocalls.yaml", "symbols-new.txt", "symbolsX.txt"):
        src = os.path.join(HERE, "..", "..", "data", fn)
        if os.path.exists(src):
            shutil.copy(src, a.work)

    lines = make_inputs(ax25ref.read_corpus(a.corpus), a.max)
    print("%d packets" % len(lines))
    out, crashes = {}, {}
    for name, b in builds:
        out[name], crashes[name] = run_decode(os.path.join(b, "src", "decode_aprs"), a.work, lines)
        print("%-14s decoded %d, crashed on %d" % (name, len(out[name]), len(crashes[name])))

    dut = builds[0][0]
    rows = []
    unexplained = 0
    for name, _ in builds[1:]:
        cats = {}
        for i, s in enumerate(lines):
            x, y = out[dut].get(i, ""), out[name].get(i, "")
            if x == y:
                continue
            if i in crashes[name] and i not in crashes[dut]:
                cat = "%s crashes, %s does not" % (name, dut)
            elif i in crashes[dut]:
                cat = "UNEXPLAINED: %s crashes" % dut
            else:
                cat = None
                for label, norm in EXPLAINED:
                    if norm(x) == norm(y):
                        cat = label
                        break
                if cat is None and name.endswith("dev"):
                    cat = "upstream dev change (not in 1.8.2)"
                if cat is None:
                    cat = "UNEXPLAINED difference"
            cats.setdefault(cat, []).append(i)
        print("\n%s vs %s: %d of %d packets decoded identically" %
              (dut, name, len(lines) - sum(len(v) for v in cats.values()), len(lines)))
        for cat, idx in sorted(cats.items()):
            print("  %-55s %5d   e.g. %r" % (cat, len(idx), lines[idx[0]][:70]))
            if cat.startswith("UNEXPLAINED"):
                unexplained += len(idx)
                for k in idx[:5]:
                    print("      %r\n        %s: %r\n        %s: %r" % (lines[k][:80], dut, out[dut].get(k, "")[-160:],
                                                                   name, out[name].get(k, "")[-160:]))
            rows.append(dict(compare="%s vs %s" % (dut, name), category=cat, count=len(idx), example=lines[idx[0]]))

    # Independent interpretation check.
    try:
        import aprslib
    except ImportError:
        aprslib = None
        print("\naprslib not installed: independent position check skipped")
    if aprslib:
        corpus = set(ax25ref.read_corpus(a.corpus))
        cnt = {"agree": 0, "ambiguity convention": 0, "differ, corpus packet": 0, "differ, mutated packet": 0}
        bad = []
        for i, s in enumerate(lines):
            if "<0x" in s:
                continue
            try:
                r = aprslib.parse(s)
            except Exception:
                continue
            if "latitude" not in r or "longitude" not in r:
                continue
            p = dw_position(out[dut].get(i, ""))
            if p is None:
                continue
            if abs(p[0] - r["latitude"]) < 1e-4 and abs(p[1] - r["longitude"]) < 1e-4:
                cnt["agree"] += 1
            elif r.get("posambiguity"):
                # Dire Wolf shows the corner of the ambiguity box, aprslib its center.
                cnt["ambiguity convention"] += 1
            elif s in corpus:
                cnt["differ, corpus packet"] += 1
                bad.append((s, p, (r["latitude"], r["longitude"])))
            else:
                cnt["differ, mutated packet"] += 1
        print("\naprslib vs %s positions: %s" % (dut, cnt))
        for s, p, q in bad[:15]:
            print("   %r  dw=%s aprslib=%s" % (s[:70], p, q))
        for k, v in cnt.items():
            rows.append(dict(compare="aprslib positions vs " + dut, category=k, count=v, example=""))
        unexplained += cnt["differ, corpus packet"]

    vg_errors = 0
    if a.valgrind and shutil.which("valgrind"):
        path = os.path.join(a.work, "vg_in.txt")
        with open(path, "w", encoding="latin-1") as f:
            f.write("\n".join(lines) + "\n")
        p = subprocess.run(["valgrind", "--error-exitcode=9", os.path.join(builds[0][1], "src", "decode_aprs"), path],
                           cwd=a.work, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        m = re.search(rb"ERROR SUMMARY: (\d+) errors", p.stderr)
        vg_errors = int(m.group(1)) if m else -1
        print("\nvalgrind, %s decode_aprs on all %d packets: %s errors" % (dut, len(lines), vg_errors))
        rows.append(dict(compare="valgrind " + dut, category="errors", count=vg_errors, example=""))

    if a.csv:
        with open(a.csv, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=["compare", "category", "count", "example"], lineterminator="\n")
            w.writeheader()
            w.writerows(rows)
    return 1 if unexplained or crashes[dut] or vg_errors else 0


if __name__ == "__main__":
    sys.exit(main())
