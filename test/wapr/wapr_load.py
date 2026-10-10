#!/usr/bin/env python3
"""WAPR channel load simulation: throughput, delivery and latency as offered load grows.

    python3 wapr_load.py --capture ../../doc/wapr/results/stage4b/capture_H150.txt \
        --profile H150 --out load.csv [--seed 1]

Event driven model of N stations sending frames to one receiver (a gateway):

* Traffic: each station generates frames as a Poisson process; the offered load G is
  the mean number of frame airtimes generated per airtime, over all stations.
* Path SNR: station to gateway SNR_2500 uniform in [snr_lo, snr_hi] dB; station to
  station likewise.  A fraction `hidden` of station pairs cannot hear each other at all.
* Reception at the gateway: a frame needs SNR_2500 above the profile's 90 % threshold
  (stage 1/2: H150 -6.2 dB, F600 -0.2 dB) and must survive every overlapping frame.
  Survival is looked up in the capture table measured with the real C receiver
  (waprtest -c: probability of decoding the wanted frame against one interferer by
  signal to interference ratio and overlap fraction).  Several interferers: their
  powers are added and the largest overlap is used.  With --no-capture any overlap is
  fatal (classic ALOHA model, used to validate the simulator against theory).
* Channel access: 'aloha' sends at once.  'csma' is Dire Wolf's p-persistent scheme
  (SLOTTIME 0.1 s, PERSIST 63, i.e. p = 0.25) with WAPR's energy busy detector, which
  sees a transmission when the listening station receives it at SNR_2500 >= dcd_snr
  (0 dB: derived from the detector's +6 dB in a band of about M+1 tone spacings).

Output: per access method, hidden fraction and load: offered load G, throughput S
(frames decoded at the gateway per airtime; mean, min and max over the station
layouts), delivery probability, median and 95th percentile latency (generation to end
of the decoded transmission, in airtimes; the 95th percentile is the worst layout's).
Every load and access method uses the same station layouts (--topologies of them).
Seeds are fixed; results repeat exactly.
"""

import argparse
import csv
import heapq
import math
import re

import numpy as np

PROFILES = {'H150': dict(airtime=2.667, thr=-6.24), 'F600': dict(airtime=0.667, thr=-0.16),
            'R25': dict(airtime=11.96, thr=-13.35)}


def load_capture(path):
    """Table of P(wanted decoded) by (overlap, SIR) from waprtest -c output."""
    rows = {}
    for line in open(path):
        m = re.search(r'SIR=(-?[\d.]+) dB.*overlap=([\d.]+): wanted (\d+) / (\d+)', line)
        if m:
            rows.setdefault(float(m.group(2)), {})[float(m.group(1))] = int(m.group(3)) / int(m.group(4))
    ovs = sorted(rows)
    sirs = sorted(rows[ovs[0]])
    table = np.array([[rows[o][s] for s in sirs] for o in ovs])
    return np.array(ovs), np.array(sirs), table


def capture_prob(cap, sir, overlap):
    ovs, sirs, table = cap
    i = int(np.clip(np.searchsorted(ovs, overlap), 0, len(ovs) - 1))   # next measured overlap up (pessimistic)
    row = table[i]
    if sir <= sirs[0]:
        return row[0]
    if sir >= sirs[-1]:
        return row[-1]
    return float(np.interp(sir, sirs, row))


def topology(a, hidden, seed, k):
    """Station to gateway SNRs and who hears whom.  The SNRs depend only on (seed, k), so
    every load, access method and hidden fraction uses the same layouts."""
    N = a.stations
    r = np.random.default_rng([seed, 7, k])
    snr_gw = r.uniform(a.snr_lo, a.snr_hi, N)
    snr_ss = r.uniform(a.snr_lo, a.snr_hi, (N, N))
    hid = np.random.default_rng([seed, 8, k]).random((N, N)) < hidden
    hid = hid | hid.T
    np.fill_diagonal(hid, False)
    hears = (~hid) & (snr_ss >= a.dcd_snr)  # hears[i, j]: i's busy detector sees j
    return snr_gw, hears


def simulate(a, cap, access, topo, G, rng):
    T = a.airtime
    N = a.stations
    lam = G / (N * T)                       # frames per second per station
    horizon = a.frames / (G / T)            # long enough for about a.frames generated frames
    snr_gw, hears = topo

    events = []                              # (time, kind, station, frame id)
    for s in range(N):
        heapq.heappush(events, (rng.exponential(1 / lam), 0, s, -1))
    queue = [[] for _ in range(N)]          # generation times waiting
    busy_until = np.zeros(N)                 # own transmission end
    on_air = []                              # (start, end, station, gen_time)
    finished = []                            # completed transmissions
    sent = 0

    def channel_busy(s, t):
        return any(st <= t < en and hears[s, j] for st, en, j, _ in on_air)

    def try_send(s, t):
        nonlocal sent
        if not queue[s] or busy_until[s] > t:
            return
        if access == 'csma' and (channel_busy(s, t) or rng.random() > a.persist):
            heapq.heappush(events, (t + a.slottime, 1, s, -1))
            return
        g = queue[s].pop(0)
        on_air.append((t, t + T, s, g))
        busy_until[s] = t + T
        sent += 1
        heapq.heappush(events, (t + T, 2, s, -1))

    while events:
        t, kind, s, _ = heapq.heappop(events)
        if t > horizon:
            break
        if kind == 0:                        # new frame generated
            queue[s].append(t)
            heapq.heappush(events, (t + rng.exponential(1 / lam), 0, s, -1))
            try_send(s, t)
        elif kind == 1:                      # back off over
            try_send(s, t)
        else:                                # own transmission ended
            try_send(s, t)
        # drop transmissions long finished from the on air list
        if len(on_air) > 200:
            keep = [x for x in on_air if x[1] > t - 2 * T]
            finished.extend(x for x in on_air if x[1] <= t - 2 * T)
            on_air[:] = keep
    finished.extend(on_air)

    # Reception at the gateway, after the fact.
    finished.sort()
    starts = np.array([x[0] for x in finished])
    ok = 0
    lat = []
    gen = 0
    for k, (st, en, s, g) in enumerate(finished):
        if en > horizon:
            continue
        gen += 1
        if snr_gw[s] < a.thr_snr:
            continue
        lo = np.searchsorted(starts, st - T)
        hi = np.searchsorted(starts, en)
        ip = 0.0
        ov = 0.0
        for j in range(lo, hi):
            if j == k:
                continue
            st2, en2, s2, _ = finished[j]
            o = max(0.0, min(en, en2) - max(st, st2)) / T
            if o > 0:
                ip += 10 ** (snr_gw[s2] / 10)
                ov = max(ov, o)
        if ov > 0:
            if a.no_capture:
                continue
            sir = snr_gw[s] - 10 * math.log10(ip)
            if rng.random() > capture_prob(cap, sir, ov):
                continue
        ok += 1
        lat.append((en - g) / T)
    dur = horizon / T
    lat = np.array(lat) if lat else np.array([np.nan])
    return dict(G_offered=G, G_sent=sent / dur, S=ok / dur, delivery=ok / max(gen, 1),
                lat_med=float(np.median(lat)), lat_p95=float(np.percentile(lat, 95)) if len(lat) > 1 else float('nan'))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--capture', required=True)
    ap.add_argument('--profile', default='H150', choices=sorted(PROFILES))
    ap.add_argument('--stations', type=int, default=20)
    ap.add_argument('--frames', type=int, default=4000, help='frames generated per load point (about)')
    ap.add_argument('--loads', default='0.05,0.1,0.2,0.3,0.5,0.7,1.0,1.5,2.0')
    ap.add_argument('--snr-lo', type=float, default=-3.0)
    ap.add_argument('--snr-hi', type=float, default=27.0)
    ap.add_argument('--dcd-snr', type=float, default=0.0)
    ap.add_argument('--slottime', type=float, default=0.1)
    ap.add_argument('--persist', type=float, default=0.25)
    ap.add_argument('--hidden', default='0,0.3')
    ap.add_argument('--no-capture', action='store_true')
    ap.add_argument('--topologies', type=int, default=5, help='random station layouts averaged per point')
    ap.add_argument('--seed', type=int, default=1)
    ap.add_argument('--out', default='wapr_load.csv')
    a = ap.parse_args()
    a.airtime = PROFILES[a.profile]['airtime']
    a.thr_snr = PROFILES[a.profile]['thr']
    cap = load_capture(a.capture)

    rows = []
    for access in ('aloha', 'csma'):
        for hidden in (float(h) for h in a.hidden.split(',')):
            for G in (float(g) for g in a.loads.split(',')):
                runs = []
                for k in range(a.topologies):
                    topo = topology(a, hidden, a.seed, k)
                    rng = np.random.default_rng([a.seed, k, int(G * 1000), int(hidden * 1000), access == 'csma', a.no_capture])
                    runs.append(simulate(a, cap, access, topo, G, rng))
                r = {key: float(np.mean([x[key] for x in runs])) for key in ('G_sent', 'S', 'delivery')}
                r['G_offered'] = G
                # latency: pooled over topologies (medians of medians would hide the tail)
                r['lat_med'] = float(np.median([x['lat_med'] for x in runs]))
                r['lat_p95'] = float(np.max([x['lat_p95'] for x in runs]))
                r['S_min'] = float(np.min([x['S'] for x in runs]))
                r['S_max'] = float(np.max([x['S'] for x in runs]))
                r.update(access=access, hidden=hidden, capture=not a.no_capture, profile=a.profile,
                         topologies=a.topologies, seed=a.seed)
                rows.append(r)
                print('%-5s hidden %.1f G %.2f: sent %.2f S %.3f [%.3f-%.3f] delivery %.3f latency median %.2f p95(max) %.2f airtimes' % (
                    access, hidden, G, r['G_sent'], r['S'], r['S_min'], r['S_max'], r['delivery'], r['lat_med'], r['lat_p95']))
    with open(a.out, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()), lineterminator='\n')
        w.writeheader()
        for r in rows:
            w.writerow({k: (round(v, 4) if isinstance(v, float) else v) for k, v in r.items()})


if __name__ == '__main__':
    main()
