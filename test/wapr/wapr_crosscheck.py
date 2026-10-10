#!/usr/bin/env python3
"""Decode identical audio with the Python reference and the C receiver.

    python3 wapr_crosscheck.py WAPR_TEST_BINARY SYSTEM SNR_DB FRAMES [CONDITION] [SEED]

Buffers are made exactly as in wapr_bench.py (frame, random lead in, channel, noise),
written to a file for `wapr_test -r`, and decoded by wapr_phy.Phy.receive as well.
Prints delivery for both and the frames only one of them delivered.
"""

import os
import struct
import subprocess
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import wapr_bench as wb      # noqa: E402
import wapr_channel as ch    # noqa: E402
import wapr_frame            # noqa: E402


def main():
    if len(sys.argv) < 5:
        sys.exit(__doc__)
    binary, system, snr, frames = sys.argv[1], sys.argv[2], float(sys.argv[3]), int(sys.argv[4])
    cond = sys.argv[5] if len(sys.argv) > 5 else 'awgn'
    seed = int(sys.argv[6]) if len(sys.argv) > 6 else 7
    _, systems = wb.load_config(os.path.join(HERE, 'configs', 'stage1.json'))
    phy = wb.get_phy(system, systems[system]['profile'])
    fs = phy.fs
    py = []
    fd, path = tempfile.mkstemp(suffix='.f32')
    with os.fdopen(fd, 'wb') as f:
        for i in range(frames):
            r = wb.rng_for(seed, 'crosscheck', i)
            body = bytes(r.integers(32, 127, int(r.integers(0, 33))).astype(np.uint8))
            info = wapr_frame.pack(1, 'N%dTST' % (i % 10), '', i % 1024, body)
            x = phy.modulate(phy.encode_bits(info))
            lead = int(r.uniform(0.25, 1.0) * phy.airtime * fs) + phy.sps
            buf = np.concatenate([np.zeros(lead), x, np.zeros(int(0.25 * phy.airtime * fs) + phy.sps)])
            ps = float(np.mean(x ** 2))
            y, _ = ch.apply_channel(buf, fs, cond, r)
            y = y + r.standard_normal(len(y)) * ch.noise_sigma(ps, fs, snr)
            out, _ = phy.receive(y)
            py.append(any(np.array_equal(o, info) for o in out))
            f.write(struct.pack('<i', len(y)))
            f.write(y.astype('<f4').tobytes())
            f.write(bytes(np.packbits(info)))
    name = system.split('_')[0]
    res = subprocess.run([binary, '-r', name, str(fs), path], stdout=subprocess.PIPE, check=True).stdout.decode()
    os.remove(path)
    c = [int(l.split()[1]) == 1 for l in res.split('\n') if l.strip()]
    cbad = sum(int(l.split()[2]) for l in res.split('\n') if l.strip())
    assert len(c) == frames
    print('%s %s SNR %.2f dB, %d frames: Python %d, C %d (C wrong %d); only Python %d, only C %d' % (
        system, cond, snr, frames, sum(py), sum(c), cbad,
        sum(a and not b for a, b in zip(py, c)), sum(b and not a for a, b in zip(py, c))))


if __name__ == '__main__':
    main()
