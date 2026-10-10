#!/usr/bin/env python3
"""Acknowledgement and retransmission between two real direwolf processes on WAPR.

    python3 wapr_link_test.py BUILD_DIR WORK_DIR [--profile F600]

Station A (N0AAA) and station B (N0BBB) each have one MODEM WAPR channel; their
transmit audio is carried to the other in real time (test/compat/dw_session.Channel).
Chosen transmissions are lost on the way:

  1. A sends a message to B (asks for an acknowledgement).  B's first acknowledgement
     is lost: A must send the message again, B must not deliver it twice but must
     acknowledge again, and A must then stop.
  2. A sends a position (broadcast): B delivers it once, nobody acknowledges it.
  3. A sends a second message; its first transmission is lost: A sends it again after
     the timeout, B delivers it once and acknowledges.

Linux only (ALSA file plugin, FIFOs).
"""

import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'compat'))

import ax25ref            # noqa: E402
import dw_session as D    # noqa: E402

FAILS = []
RATE = 48000


def check(name, cond, detail=''):
    print('%-4s %s %s' % ('ok' if cond else 'FAIL', name, detail))
    if not cond:
        FAILS.append(name)


class LossyChannel(D.Channel):
    """A Channel that silences whole transmissions by number (1 = first)."""

    def __init__(self, fifo_path, rate, drop, tap_path=None):
        super().__init__(fifo_path, rate, tap_path)
        self.drop = set(drop)
        self.burst = 0
        self.last = 0.0
        self.bursts = []

    def run(self):
        t0 = time.time()
        sent = 0
        while not self.stop_flag:
            try:
                d = os.read(self.fd, 1 << 20)
                if d:
                    now = time.time()
                    if now - self.last > 0.4:          # a new transmission
                        self.burst += 1
                        self.bursts.append(now)
                    self.last = now
                    if self.burst in self.drop:
                        d = bytes(len(d))
                    self.buf += d
            except BlockingIOError:
                pass
            want = int((time.time() - t0) * self.rate) - sent
            if want > 0:
                n = min(want * 2, len(self.buf)) // 2 * 2
                chunk = bytes(self.buf[:n]) + bytes(want * 2 - n)
                del self.buf[:n]
                sent += want
                try:
                    if self.dest:
                        self.dest.write(chunk)
                        self.dest.flush()
                except (BrokenPipeError, ValueError):
                    break
            time.sleep(0.01)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('build')
    ap.add_argument('work')
    ap.add_argument('--profile', default='F600')
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)
    os.makedirs('/tmp/dwt', exist_ok=True)
    os.chmod('/tmp/dwt', 0o1777)
    exe = os.path.join(a.build, 'src', 'direwolf')

    # A's transmissions: 1 message 1, 2 message 1 again, 3 position, 4 message 2 (lost), 5 message 2 again
    # B's transmissions: 1 ack 1 (lost), 2 ack 1 again, 3 ack 2
    ab = LossyChannel('/tmp/dwt/wab', RATE, drop=[4])
    ba = LossyChannel('/tmp/dwt/wba', RATE, drop=[1])
    conf = lambda call, tx, kp, agw: ['ADEVICE stdin file:' + tx, 'ARATE %d' % RATE, 'CHANNEL 0',
                                      'MYCALL ' + call, 'MODEM WAPR ' + a.profile, 'TXDELAY 20', 'TXTAIL 5',
                                      'KISSPORT %d' % kp, 'AGWPORT %d' % agw]
    ka, kb = D.free_port(), D.free_port()
    A = D.Direwolf(exe, conf('N0AAA', '/tmp/dwt/wab', ka, D.free_port()), a.work, 'A')
    B = D.Direwolf(exe, conf('N0BBB', '/tmp/dwt/wba', kb, D.free_port()), a.work, 'B')
    ab.connect(B.p.stdin)
    ba.connect(A.p.stdin)
    ab.start()
    ba.start()
    timeout = 3 * {'F600': 0.667, 'H150': 2.667, 'R25': 11.96}[a.profile] + 3
    try:
        kA, kB = D.KissClient(ka), D.KissClient(kb)
        time.sleep(3)
        kA.send(ax25ref.tnc2_to_frame('N0AAA>APDW18::N0BBB    :hello one{1'))
        kB.poll(4 * timeout + 10)
        kA.send(ax25ref.tnc2_to_frame('N0AAA>APDW18:!4237.14N/07120.83W-position'))
        kB.poll(timeout + 5)
        kA.send(ax25ref.tnc2_to_frame('N0AAA>APDW18::N0BBB    :hello two{2'))
        kB.poll(4 * timeout + 10)
        kA.poll(1)
    finally:
        A.stop()
        B.stop()
        ab.stop()
        ba.stop()

    la, lb = A.output(), B.output()
    got = [ax25ref.frame_to_tnc2(f) for _, cmd, f in kB.frames if cmd == 0]
    want = ['N0AAA>APZWAP::N0BBB    :hello one{1', 'N0AAA>APZWAP:!4237.14N/07120.83W-position',
            'N0AAA>APZWAP::N0BBB    :hello two{2']
    check('B delivered each frame exactly once, in order', got == want, repr(got))
    check('A sent message 1 again after the lost acknowledgement, and message 2 after the lost frame',
          la.count('sending it again') == 2, '%d resends' % la.count('sending it again'))
    check('A saw both messages acknowledged', la.count('N0BBB acknowledged frame') == 2,
          '%d' % la.count('N0BBB acknowledged frame'))
    check('B suppressed the copy of message 1 and acknowledged it again',
          lb.count('suppressed, acknowledged again') == 1, '%d' % lb.count('suppressed, acknowledged again'))
    check('A did not give up', 'gave up' not in la)
    check('transmissions on air: A 5, B 3', ab.burst == 5 and ba.burst == 3, 'A %d, B %d' % (ab.burst, ba.burst))
    print('%d failure(s)' % len(FAILS))
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
