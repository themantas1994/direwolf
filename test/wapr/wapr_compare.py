#!/usr/bin/env python3
"""Apply the stage 1 decision rule to two systems' 90 % thresholds.

    python3 wapr_compare.py WAPR_RESULTS.csv WAPR_SYSTEM REF_RESULTS.csv REF_SYSTEM CONDITION

Delta = reference threshold - WAPR threshold (positive: WAPR needs less SNR).  The
interval comes from the independent bootstrap replicates saved by wapr_bench.py.  If the
reference never reaches 90 % on its grid, Delta is a lower bound (grid max - WAPR).
"""

import csv
import sys

import numpy as np


def load(prefix, system, cond, target=0.9):
    base = prefix[:-4] if prefix.endswith('.csv') else prefix
    summ = [r for r in csv.DictReader(open(base + '_summary.csv'))
            if r['system'] == system and r['condition'] == cond and abs(float(r['target']) - target) < 1e-9]
    if not summ:
        sys.exit('no summary row for %s %s in %s' % (system, cond, base))
    reps = np.load(base + '_bootstrap.npz')['%s|%s|%g' % (system, cond, target)]
    rows = [r for r in csv.DictReader(open(base + '.csv')) if r['system'] == system and r['condition'] == cond]
    return summ[0], reps, max(float(r['snr_db']) for r in rows)


def main():
    if len(sys.argv) != 6:
        sys.exit(__doc__)
    wf, ws, rf, rs, cond = sys.argv[1:]
    w, wr, _ = load(wf, ws, cond)
    r, rr, rmax = load(rf, rs, cond)
    west = float(w['snr_db'])
    rest = float(r['snr_db'])
    bound = False
    if not np.isfinite(rest) and r['note'].startswith('not reached'):
        rest = rmax
        rr = np.full_like(wr, rmax)
        bound = True
    d = rest - west
    dd = rr - wr
    ok = np.isfinite(dd)
    lo, hi = np.percentile(dd[ok], [2.5, 97.5]) if ok.mean() > 0.9 else (float('nan'),) * 2
    if not np.isfinite(d) or not np.isfinite(lo):
        verdict = 'undetermined (threshold or interval missing)'
    elif d >= 1.0 and lo >= 1.0:
        verdict = 'strongly supported'
    elif d >= 1.0 and lo > 0:
        verdict = 'supported'
    else:
        verdict = 'not supported'
    print('%s vs %s on %s: WAPR %.2f dB, reference %s%.2f dB, Delta %s%.2f dB [%.2f, %.2f] -> %s%s' % (
        ws, rs, cond, west, '>' if bound else '', rest, '>' if bound else '', d, lo, hi, verdict,
        ' (reference has an error floor; Delta is a lower bound)' if bound else ''))


if __name__ == '__main__':
    main()
