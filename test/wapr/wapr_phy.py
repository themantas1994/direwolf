"""WAPR research simulator: framing, GFSK M-FSK modulation and receiver.

A profile is a plain dict (see configs/*.json).  Frame on air:

    [Costas sync] [data] [Costas sync] [data] ... [Costas sync]

data = Gray mapped M-FSK symbols of the interleaved codeword of
       scramble(header + payload + CRC-24).

FT8 / FT4 (Franke, Somerville, Taylor, QEX July/Aug 2020) are the model for Costas
sync blocks, GFSK frequency shaping and soft decision decoding of non-coherent M-FSK.
Nothing is copied: the Costas arrays here are the lexicographically first ones of each
order, found by search, and the codes are in wapr_codes.py.
"""

import itertools
import math

import numpy as np

import wapr_codes


# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------

def costas_array(order):
    """Lexicographically first Costas permutation of the given order."""
    for p in itertools.permutations(range(order)):
        vecs = set()
        ok = True
        for i in range(order):
            for j in range(i + 1, order):
                v = (j - i, p[j] - p[i])
                if v in vecs:
                    ok = False
                    break
                vecs.add(v)
            if not ok:
                break
        if ok:
            return list(p)
    raise ValueError('no Costas array of order %d' % order)


def gray(m):
    return m ^ (m >> 1)


def log_i0(x):
    """log of the modified Bessel function I0, for x >= 0, without overflow."""
    x = np.asarray(x, dtype=np.float64)
    small = x < 50.0
    out = np.empty_like(x)
    out[small] = np.log(np.i0(x[small]))
    xl = x[~small]
    out[~small] = xl - 0.5 * np.log(2 * math.pi * xl) + np.log1p(1.0 / (8.0 * xl))
    return out


def logsumexp(a, axis):
    m = np.max(a, axis=axis, keepdims=True)
    return np.squeeze(m, axis=axis) + np.log(np.sum(np.exp(a - m), axis=axis))


_erf = np.vectorize(math.erf)


def gfsk_pulse(bt, sps):
    """Frequency pulse of a GFSK symbol, 3 symbols long, area 1 symbol."""
    t = (np.arange(3 * sps) - 1.5 * sps + 0.5) / sps
    c = math.pi * math.sqrt(2.0 / math.log(2.0))
    return 0.5 * (_erf(c * bt * (t + 0.5)) - _erf(c * bt * (t - 0.5)))


# ----------------------------------------------------------------------------
# Profile
# ----------------------------------------------------------------------------

class Phy:
    """Everything derived from a profile dict that the transmitter and receiver share."""

    def __init__(self, prof):
        self.prof = prof
        self.fs = prof['fs']
        self.M = prof['M']
        self.bps = int(round(math.log2(self.M)))
        assert 1 << self.bps == self.M
        self.baud = prof['baud']
        self.sps = int(round(self.fs / self.baud))
        assert self.sps * self.baud == self.fs, 'fs must be a multiple of the symbol rate'
        self.h = prof.get('h', 1.0)
        self.spacing = self.h * self.baud
        self.tones = prof['f_center'] + (np.arange(self.M) - (self.M - 1) / 2.0) * self.spacing
        self.k_info = 8 * (prof['header_bytes'] + prof['payload_bytes']) + prof['crc_bits']
        fec = prof['fec']
        self.code = wapr_codes.make_code(fec['type'], self.k_info, fec.get('rate', 0.5),
                                         fec.get('dv', 3), fec.get('seed', 1), fec.get('max_iter', 50))
        self.n_coded = self.code.n
        self.n_data = -(-self.n_coded // self.bps)
        il = prof.get('interleave_seed')
        self.perm = (np.random.default_rng(il).permutation(self.n_coded) if il is not None
                     else np.arange(self.n_coded))
        self.scr = wapr_codes.prbs(self.k_info)
        # Sync layout
        self.costas = costas_array(self.M)
        nb = prof['sync']['blocks']
        if nb == 1:
            parts = [self.n_data]
        else:
            q, r = divmod(self.n_data, nb - 1)
            parts = [q + (1 if i < r else 0) for i in range(nb - 1)]
        layout = []          # (is_sync, index) per symbol
        for b in range(nb):
            layout += [(True, j) for j in range(self.M)]
            if b < len(parts):
                layout += [(False, 0)] * parts[b]
        self.n_sym = len(layout)
        self.sync_pos = np.array([i for i, (s, _) in enumerate(layout) if s])
        self.sync_tone = np.array([self.costas[j] for (s, j) in layout if s])
        self.data_pos = np.array([i for i, (s, _) in enumerate(layout) if not s])
        self.airtime = self.n_sym / self.baud
        # Gray label bits of each tone, MSB first: bits[m, i]
        self.labels = np.array([[(gray(m) >> (self.bps - 1 - i)) & 1 for i in range(self.bps)]
                                for m in range(self.M)], dtype=np.uint8)
        # tone index for each bit pattern value v: inverse Gray
        inv = np.zeros(self.M, dtype=np.int64)
        for m in range(self.M):
            inv[gray(m)] = m
        self.inv_gray = inv
        bt = prof.get('bt')
        self.pulse = gfsk_pulse(bt, self.sps) if bt else None

    # ------------------------------------------------------------------
    # Transmit
    # ------------------------------------------------------------------

    def encode_bits(self, info):
        """info: header + payload bits.  Returns transmitted symbol (tone) sequence."""
        assert len(info) + self.prof['crc_bits'] == self.k_info
        msg = np.concatenate([info, wapr_codes.crc_bits(info, self.prof['crc_bits'])]) ^ self.scr
        coded = self.code.encode(msg)
        tx = coded[self.perm]
        pad = self.n_data * self.bps - len(tx)
        tx = np.concatenate([tx, np.zeros(pad, dtype=np.uint8)])
        vals = tx.reshape(-1, self.bps) @ (1 << np.arange(self.bps - 1, -1, -1))
        syms = np.empty(self.n_sym, dtype=np.int64)
        syms[self.data_pos] = self.inv_gray[vals]
        syms[self.sync_pos] = self.sync_tone
        return syms

    def modulate(self, syms, amp=1.0):
        """Continuous phase (G)FSK audio for a symbol sequence."""
        sps = self.sps
        d = syms - (self.M - 1) / 2.0
        n = len(syms) * sps
        if self.pulse is None:
            dev = np.repeat(d, sps)
        else:
            # pad with copies of the first / last symbol so the edges don't wander
            dd = np.concatenate([[d[0]], d, [d[-1]]])
            imp = np.zeros(len(dd) * sps)
            imp[np.arange(len(dd)) * sps] = dd
            # pulse of dd[i] covers samples i*sps .. i*sps + 3*sps; d[0] = dd[1] is
            # centred on sample 2.5*sps
            dev = np.convolve(imp, self.pulse)[2 * sps:2 * sps + n]
        f = self.prof['f_center'] + self.spacing * dev
        phase = 2 * math.pi * np.cumsum(f) / self.fs
        x = amp * np.sin(phase)
        nr = int(round(self.prof.get('ramp_symbols', 0.25) * sps))
        if nr > 0:
            w = 0.5 - 0.5 * np.cos(math.pi * (np.arange(nr) + 0.5) / nr)
            x[:nr] *= w
            x[-nr:] *= w[::-1]
        return x

    # ------------------------------------------------------------------
    # Receive
    # ------------------------------------------------------------------

    def _tone_energies(self, x, t0, fo, positions):
        """|correlation|^2 with each tone for the symbols at the given positions."""
        sps = self.sps
        idx = t0 + positions[:, None] * sps + np.arange(sps)[None, :]
        seg = x[idx]
        n = np.arange(sps)
        tm = np.exp(-2j * math.pi * np.outer(n, self.tones + fo) / self.fs)
        c = seg @ tm
        return (c.real ** 2 + c.imag ** 2)

    def coarse_search(self, x):
        rx = self.prof['rx']
        ts = rx.get('time_steps', 4)
        fos = rx.get('freq_oversample', 2)
        sps = self.sps
        hop = sps // ts
        nfft = sps * fos
        dfb = self.fs / nfft
        span = (self.n_sym - 1) * ts            # hops between first and last symbol start
        nh = (len(x) - sps) // hop + 1
        if nh <= span:
            return []
        idx = np.arange(nh)[:, None] * hop + np.arange(sps)[None, :]
        P = np.abs(np.fft.rfft(x[idx], nfft, axis=1)) ** 2
        # coarse bins cover the search range; the fine search covers the rest
        sb = int(math.floor(rx.get('freq_search_hz', 50.0) / dfb + 0.5))
        tb = np.round(self.tones / dfb).astype(int)
        if tb[0] - sb < 0 or tb[-1] + sb >= P.shape[1]:
            raise ValueError('search range outside the audio band')
        nt = nh - span
        nob = 2 * sb + 1
        num = np.zeros((nt, nob))
        den = np.zeros((nt, nob))
        for p, c in zip(self.sync_pos, self.sync_tone):
            rows = P[p * ts:p * ts + nt]
            num += rows[:, tb[c] - sb:tb[c] + sb + 1]
            for m in range(self.M):
                den += rows[:, tb[m] - sb:tb[m] + sb + 1]
        metric = self.M * num / np.maximum(den, 1e-30)
        # greedy peak picking with suppression
        order = np.argsort(metric, axis=None)[::-1]
        picks = []
        smin = rx.get('sync_min', 1.5)
        ncand = rx.get('candidates', 4)
        for flat in order:
            i, j = divmod(int(flat), nob)
            if metric[i, j] < smin:
                break
            if any(abs(i - a) <= ts and abs(j - b) <= fos for a, b, _ in picks):
                continue
            picks.append((i, j, float(metric[i, j])))
            if len(picks) >= ncand:
                break
        return [(i * hop, (j - sb) * dfb, m) for i, j, m in picks]

    def _sync_metric(self, x, t0, fos_list, use_data=False):
        """Metric for each frequency offset at start sample t0.

        Sync symbols only: energy in the expected Costas tones over the energy in all
        tones (FT8 style).  With use_data, the strongest tone of every data symbol
        counts as well (non data aided), which uses the whole frame's energy.
        """
        sps = self.sps
        if t0 < 0 or t0 + self.n_sym * sps > len(x):
            return None
        pos = np.arange(self.n_sym) if use_data else self.sync_pos
        idx = t0 + pos[:, None] * sps + np.arange(sps)[None, :]
        seg = x[idx]
        n = np.arange(sps)
        f = (self.tones[None, :] + np.asarray(fos_list)[:, None]).reshape(-1)
        c = seg @ np.exp(-2j * math.pi * np.outer(n, f) / self.fs)
        E = (c.real ** 2 + c.imag ** 2).reshape(len(pos), len(fos_list), self.M)
        if use_data:
            Es = E[self.sync_pos]
            num = Es[np.arange(len(self.sync_pos)), :, self.sync_tone].sum(axis=0)
            num = num + E[self.data_pos].max(axis=2).sum(axis=0)
        else:
            num = E[np.arange(len(self.sync_pos)), :, self.sync_tone].sum(axis=0)
        den = E.sum(axis=(0, 2))
        return self.M * num / np.maximum(den, 1e-30)

    def refine(self, x, t0, fo):
        rx = self.prof['rx']
        ts = rx.get('time_steps', 4)
        fos = rx.get('freq_oversample', 2)
        fine = rx.get('fine_steps', 8)
        hop = self.sps // ts
        dfb = self.baud * 1.0 / fos
        lim = rx.get('freq_search_hz', 50.0)
        half = min(dfb / 2.0, lim)
        fl = np.clip(fo + half * (np.arange(-fine, fine + 1) / float(fine)), -lim, lim)
        fl = np.unique(fl)
        best = (-1.0, t0, fo)
        tstep = max(1, hop // fine)
        for dt in range(-hop // 2, hop // 2 + 1, tstep):
            m = self._sync_metric(x, t0 + dt, fl, rx.get('refine_data', False))
            if m is None:
                continue
            j = int(np.argmax(m))
            if m[j] > best[0]:
                best = (float(m[j]), t0 + dt, float(fl[j]))
        return best

    def soft_bits(self, E):
        """Bit LLRs from data symbol tone energies (D x M)."""
        M = self.M
        mx = E.max(axis=1)
        sig2 = max((E.sum() - mx.sum()) / (E.shape[0] * (M - 1)), 1e-30)
        a2 = np.maximum(mx - sig2, 0.0)
        w = self.prof['rx'].get('amp_window', 0)
        if w and w < len(a2):
            k = np.ones(w) / w
            a2 = np.convolve(np.pad(a2, (w // 2, w - 1 - w // 2), mode='edge'), k, mode='valid')
        else:
            a2 = np.full_like(a2, a2.mean())
        a2 = np.maximum(a2, 0.01 * sig2)
        ll = log_i0(2.0 * np.sqrt(a2)[:, None] * np.sqrt(E) / sig2)
        out = np.empty((E.shape[0], self.bps))
        for i in range(self.bps):
            z = self.labels[:, i] == 0
            out[:, i] = logsumexp(ll[:, z], 1) - logsumexp(ll[:, ~z], 1)
        return out.reshape(-1)

    def decode_at(self, x, t0, fo):
        """Demodulate and decode one candidate.  Returns info bits or None."""
        if t0 < 0 or t0 + self.n_sym * self.sps > len(x):
            return None
        E = self._tone_energies(x, t0, fo, self.data_pos)
        llr_tx = self.soft_bits(E)[:self.n_coded]
        llr = np.empty(self.n_coded)
        llr[self.perm] = llr_tx
        bits, ok = self.code.decode(llr)
        if not ok:
            return None
        msg = bits ^ self.scr
        nc = self.prof['crc_bits']
        info = msg[:-nc]
        if not np.array_equal(wapr_codes.crc_bits(info, nc), msg[-nc:]):
            return None
        return info

    def receive(self, x):
        """Search a buffer and decode the strongest candidates.

        Returns (list of decoded info bit arrays, number of sync candidates tried).
        """
        cands = self.coarse_search(x)
        out = []
        tried = 0
        for t0, fo, _ in cands:
            _, t1, f1 = self.refine(x, t0, fo)
            tried += 1
            info = self.decode_at(x, t1, f1)
            if info is not None:
                if not any(np.array_equal(info, o) for o in out):
                    out.append(info)
                if self.prof['rx'].get('stop_on_first', True):
                    break
        return out, tried
