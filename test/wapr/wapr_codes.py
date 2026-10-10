"""WAPR research simulator: integrity check, scrambler and forward error correction.

Everything here is written for this project from textbook descriptions.  No code or
code tables were taken from WSJT-X (GPL v3) or any other project:

* CRC-24 with the polynomial 0x864CFB (published in RFC 4880 / 3GPP TS 36.212),
  initial value and final XOR 0xFFFFFF so an all-zero codeword is never accepted.
* Convolutional code K=7, generators 171/133 octal (the classic NASA code), terminated,
  decoded with a soft decision Viterbi decoder.
* Irregular repeat accumulate (IRA) LDPC code: a staircase parity part (linear time
  encoding) plus an information part built by progressive edge growth (PEG,
  Hu / Eleftheriou / Arnold 2005) from a fixed seed, decoded by sum-product
  belief propagation with early stopping on a zero syndrome.

Bit arrays are numpy uint8 0/1.  LLR convention: positive means bit 0 is more likely.
"""

import functools
import math

import numpy as np


# ----------------------------------------------------------------------------
# CRC-24 and scrambler
# ----------------------------------------------------------------------------

CRC24_POLY = 0x864CFB


def crc24(bits):
    """CRC over a bit array, MSB first.  Returns 24 bits."""
    reg = 0xFFFFFF
    for b in bits:
        top = ((reg >> 23) & 1) ^ int(b)
        reg = (reg << 1) & 0xFFFFFF
        if top:
            reg ^= CRC24_POLY
    reg ^= 0xFFFFFF
    return np.array([(reg >> (23 - i)) & 1 for i in range(24)], dtype=np.uint8)


def crc_bits(bits, nbits):
    if nbits == 24:
        return crc24(bits)
    raise ValueError('only a 24 bit CRC is implemented')


def prbs(n, seed=0x1FF):
    """x^9 + x^5 + 1 whitening sequence (as in many radio standards)."""
    reg = seed
    out = np.empty(n, dtype=np.uint8)
    for i in range(n):
        b = ((reg >> 8) ^ (reg >> 4)) & 1
        out[i] = b
        reg = ((reg << 1) | b) & 0x1FF
    return out


# ----------------------------------------------------------------------------
# Convolutional code, K=7 r=1/2, terminated
# ----------------------------------------------------------------------------

class ConvCode:
    def __init__(self, k_info, polys=(0o171, 0o133), K=7):
        self.k = k_info
        self.K = K
        self.polys = polys
        self.nstates = 1 << (K - 1)
        self.n = len(polys) * (k_info + K - 1)
        ns = self.nstates
        # out[s, b] = output bits when input b arrives in state s
        # (state = previous K-1 input bits, newest in bit 0).
        self.out = np.zeros((ns, 2, len(polys)), dtype=np.uint8)
        for s in range(ns):
            for b in range(2):
                reg = (s << 1) | b          # newest bit at position 0
                for i, g in enumerate(polys):
                    self.out[s, b, i] = bin(reg & g).count('1') & 1
        # Predecessors of each next state ns: s = (ns >> 1) | (x << (K-2)), input = ns & 1
        nxt = np.arange(ns)
        self.pred = np.stack([(nxt >> 1), (nxt >> 1) | (1 << (K - 2))], axis=1)
        self.inbit = nxt & 1
        # +-1 form of the outputs for each (next state, predecessor choice)
        sgn = 1.0 - 2.0 * self.out.astype(np.float64)
        self.bm_sign = np.stack([sgn[self.pred[:, 0], self.inbit], sgn[self.pred[:, 1], self.inbit]], axis=1)

    @property
    def name(self):
        return 'conv%d' % self.K

    def encode(self, u):
        u = np.concatenate([u, np.zeros(self.K - 1, dtype=np.uint8)])
        s = 0
        out = np.empty((len(u), len(self.polys)), dtype=np.uint8)
        for i, b in enumerate(u):
            out[i] = self.out[s, b]
            s = ((s << 1) | int(b)) & (self.nstates - 1)
        return out.reshape(-1)

    def decode(self, llr):
        """Soft decision Viterbi.  Returns (info bits, True)."""
        r = len(self.polys)
        L = llr.reshape(-1, r)
        nsteps = L.shape[0]
        ns = self.nstates
        pm = np.full(ns, -1e30)
        pm[0] = 0.0
        dec = np.empty((nsteps, ns), dtype=np.uint8)
        for t in range(nsteps):
            bm = (self.bm_sign * L[t]).sum(axis=2)     # (next state, predecessor choice)
            cand = pm[self.pred] + bm
            choice = np.argmax(cand, axis=1)
            dec[t] = choice
            pm = cand[np.arange(ns), choice]
        s = 0  # terminated
        bits = np.empty(nsteps, dtype=np.uint8)
        for t in range(nsteps - 1, -1, -1):
            bits[t] = self.inbit[s]
            s = self.pred[s, dec[t, s]]
        return bits[:self.k], True


# ----------------------------------------------------------------------------
# IRA LDPC code built by progressive edge growth
# ----------------------------------------------------------------------------

def _peg_far_checks(v, own, chk_vars, checks_of, m):
    """Checks farthest from variable v: unreachable ones, else those reached last."""
    seen_c = np.zeros(m, dtype=bool)
    seen_c[own] = True
    seen_v = {v}
    frontier = list(own)
    while True:
        new_c = []
        for c in frontier:
            for y in chk_vars[c]:
                if y in seen_v:
                    continue
                seen_v.add(y)
                for c2 in checks_of(y):
                    if not seen_c[c2]:
                        seen_c[c2] = True
                        new_c.append(c2)
        if not new_c:
            return np.flatnonzero(~seen_c)
        if seen_c.all():
            return np.array(sorted(new_c))
        frontier = new_c


def _peg_ira(k, m, dv, seed):
    """Return list of check-node lists for each information variable node.

    Parity variable j (0..m-1) is connected to checks j and j+1 (staircase), the last
    one to check m-1 only.  Information nodes are added by PEG: each new edge goes to a
    check node as far as possible from the variable node in the current graph,
    the least connected one among those, ties broken by a seeded random choice.
    """
    rng = np.random.default_rng(seed)
    chk_vars = [[] for _ in range(m)]          # variables on each check
    var_chks = []                              # checks on each variable (info then parity)
    for j in range(m):
        c = [j] if j == m - 1 else [j, j + 1]
        var_chks.append(c)
    # parity variables are numbered k..k+m-1 in the final code; keep a separate list
    par_chks = var_chks
    var_chks = [None] * k
    for j in range(m):
        for c in par_chks[j]:
            chk_vars[c].append(k + j)
    deg = np.array([len(v) for v in chk_vars], dtype=np.int64)

    def checks_of(v):
        return var_chks[v] if v < k else par_chks[v - k]

    for v in range(k):
        var_chks[v] = []
        for e in range(dv):
            if e == 0:
                cand = np.flatnonzero(deg == deg.min())
            else:
                cand_set = _peg_far_checks(v, var_chks[v], chk_vars, checks_of, m)
                cand = cand_set[deg[cand_set] == deg[cand_set].min()]
            c = int(rng.choice(np.sort(cand)))
            var_chks[v].append(c)
            chk_vars[c].append(v)
            deg[c] += 1
    return var_chks, par_chks


class LdpcCode:
    def __init__(self, k_info, rate=0.5, dv=3, seed=1, max_iter=50):
        self.k = k_info
        self.n = int(math.ceil(k_info / rate))
        self.m = self.n - self.k
        self.dv = dv
        self.seed = seed
        self.max_iter = max_iter
        info_chks, par_chks = _peg_ira(self.k, self.m, dv, seed)
        ev, ec = [], []
        for v, cs in enumerate(info_chks):
            for c in cs:
                ev.append(v)
                ec.append(c)
        for j, cs in enumerate(par_chks):
            for c in cs:
                ev.append(self.k + j)
                ec.append(c)
        ev = np.array(ev)
        ec = np.array(ec)
        order = np.lexsort((ev, ec))      # group edges by check
        self.ev = ev[order]
        self.ec = ec[order]
        self.cstart = np.flatnonzero(np.r_[True, self.ec[1:] != self.ec[:-1]])
        self.info_rows = [np.array(cs) for cs in info_chks]
        # Sparse H_s as edge lists for encoding
        self.hs_v = np.array([v for v, cs in enumerate(info_chks) for _ in cs])
        self.hs_c = np.array([c for cs in info_chks for c in cs])

    @property
    def name(self):
        return 'ldpc'

    def encode(self, u):
        s = np.bincount(self.hs_c, weights=u[self.hs_v], minlength=self.m).astype(np.int64) & 1
        p = np.cumsum(s) & 1
        return np.concatenate([u, p.astype(np.uint8)])

    def girth4_free(self):
        pairs = set()
        cols = [[] for _ in range(self.n)]
        for v, c in zip(self.ev, self.ec):
            cols[v].append(c)
        for cs in cols:
            cs = sorted(cs)
            for i in range(len(cs)):
                for j in range(i + 1, len(cs)):
                    if (cs[i], cs[j]) in pairs:
                        return False
                    pairs.add((cs[i], cs[j]))
        return True

    def syndrome_ok(self, hard):
        return not np.any(np.add.reduceat(hard[self.ev].astype(np.int64), self.cstart) & 1)

    def decode(self, llr):
        """Sum-product decoding.  Returns (info bits, converged)."""
        llr = np.clip(np.asarray(llr, dtype=np.float64), -30, 30)
        q = llr[self.ev].copy()            # variable to check messages
        hard = (llr < 0).astype(np.uint8)
        if self.syndrome_ok(hard):
            return hard[:self.k], True
        for _ in range(self.max_iter):
            a = np.clip(np.abs(q), 1e-9, 30.0)
            phi = -np.log(np.tanh(a / 2.0))
            neg = (q < 0).astype(np.int64)
            sphi = np.add.reduceat(phi, self.cstart)
            sneg = np.add.reduceat(neg, self.cstart)
            cnt = np.diff(np.r_[self.cstart, len(self.ec)])
            sphi_e = np.repeat(sphi, cnt) - phi
            sign = 1.0 - 2.0 * ((np.repeat(sneg, cnt) - neg) & 1)
            x = np.clip(sphi_e, 1e-9, 30.0)
            r = sign * (-np.log(np.tanh(x / 2.0)))
            tot = llr + np.bincount(self.ev, weights=r, minlength=self.n)
            hard = (tot < 0).astype(np.uint8)
            if self.syndrome_ok(hard):
                return hard[:self.k], True
            q = tot[self.ev] - r
        return hard[:self.k], False


@functools.lru_cache(maxsize=None)
def make_code(kind, k_info, rate=0.5, dv=3, seed=1, max_iter=50):
    if kind == 'conv':
        if abs(rate - 0.5) > 1e-9:
            raise ValueError('convolutional baseline is rate 1/2 only')
        return ConvCode(k_info)
    if kind == 'ldpc':
        return LdpcCode(k_info, rate=rate, dv=dv, seed=seed, max_iter=max_iter)
    raise ValueError(kind)
