#!/usr/bin/env python3
"""End to end test of a real direwolf process with a MODEM WAPR channel.

    python3 wapr_live.py BUILD_DIR WORK_DIR [--profile H150] [--pace 2]

1. gen_packets -W makes audio of test/wapr/golden/wapr_check_msgs.txt, sent to
   direwolf over UDP (ADEVICE udp:) at `pace` times real time, so the background
   decoder thread runs as it would with a sound card.
2. A KISS TCP client must receive exactly the representable frames, each once.
3. A fake APRS-IS server is configured as the IGate: nothing heard on the WAPR
   channel may be gated to it.
4. Two packets are sent through KISS for transmission: one WAPR can carry, one
   with a digipeater path that it must refuse.  The transmit audio (ALSA file
   plugin) is decoded with atest -W: exactly the first must be there.
5. The log must not report the decoder falling behind.

Linux only (ALSA file plugin).  Exit status 0 when every check passes.
"""

import argparse
import os
import re
import socket
import subprocess
import sys
import threading
import time
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'compat'))

import ax25ref            # noqa: E402
import dw_session as D    # noqa: E402

FAILS = []


def check(name, cond, detail=''):
    print('%-4s %s %s' % ('ok' if cond else 'FAIL', name, detail))
    if not cond:
        FAILS.append(name)


class FakeIS(threading.Thread):
    """Accepts one APRS-IS client and records every line it sends."""

    def __init__(self):
        super().__init__(daemon=True)
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(('127.0.0.1', 0))
        self.srv.listen(1)
        self.port = self.srv.getsockname()[1]
        self.lines = []
        self.connected = False

    def run(self):
        c, _ = self.srv.accept()
        self.connected = True
        c.sendall(b'# fake aprs-is\r\n')
        buf = b''
        while True:
            d = c.recv(4096)
            if not d:
                return
            buf += d
            while b'\n' in buf:
                line, buf = buf.split(b'\n', 1)
                line = line.strip(b'\r').decode('latin-1')
                self.lines.append(line)
                if line.startswith('user '):
                    c.sendall(b'# logresp N0CALL-1 verified, server FAKE\r\n')


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('build')
    ap.add_argument('work')
    ap.add_argument('--profile', default='H150')
    ap.add_argument('--pace', type=float, default=2.0)
    ap.add_argument('--rate', type=int, default=48000)
    a = ap.parse_args()

    os.makedirs(a.work, exist_ok=True)
    gen = os.path.join(a.build, 'src', 'gen_packets')
    atest = os.path.join(a.build, 'src', 'atest')
    dw = os.path.join(a.build, 'src', 'direwolf')
    msgs = os.path.join(HERE, 'golden', 'wapr_check_msgs.txt')

    # Expected: messages WAPR can carry (no path, information part with newline <= 32 bytes).
    expected = []
    for line in open(msgs):
        line = line.rstrip('\n')
        head, info = line.split(':', 1)
        src, dst = head.split('>')
        if ',' in dst or len(info) + 1 > 32:
            continue
        expected.append((src, info + '\n'))

    rxwav = os.path.join(a.work, 'rx.wav')
    subprocess.run([gen, '-W', a.profile, '-r', str(a.rate), '-o', rxwav, msgs], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    raw, rate = D.wav_samples(rxwav)
    assert rate == a.rate

    fis = FakeIS()
    fis.start()
    udp = D.free_port()
    kp, agw = D.free_port(), D.free_port()
    # direwolf truncates audio device names at 29 characters: keep this short
    # (same convention as test/compat/interfaces.py).
    os.makedirs('/tmp/dwt', exist_ok=True)
    txraw = '/tmp/dwt/waprtx'
    if os.path.exists(txraw):
        os.remove(txraw)
    conf = ['ADEVICE udp:%d file:%s' % (udp, txraw), 'ARATE %d' % a.rate, 'CHANNEL 0', 'MYCALL N0CALL-1',
            'MODEM WAPR %s' % a.profile, 'TXDELAY 20', 'TXTAIL 5', 'KISSPORT %d' % kp, 'AGWPORT %d' % agw,
            'IGSERVER 127.0.0.1:%d' % fis.port, 'IGLOGIN N0CALL-1 13023']
    p = D.Direwolf(dw, conf, a.work, name='wapr')
    try:
        k = D.KissClient(kp)
        t0 = time.time()
        while not fis.connected and time.time() - t0 < 30:
            time.sleep(0.2)
        check('direwolf logged in to the fake APRS-IS', fis.connected)

        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        chunk = 1024
        t0 = time.time()
        for i in range(0, len(raw), chunk):
            s.sendto(raw[i:i + chunk], ('127.0.0.1', udp))
            target = t0 + (i + chunk) / 2 / a.rate / a.pace
            while time.time() < target:
                time.sleep(0.0005)
        # silence so the last windows complete
        silence = bytes(int(a.rate * 8))
        for i in range(0, len(silence), chunk):
            s.sendto(silence[i:i + chunk], ('127.0.0.1', udp))
            time.sleep(chunk / 2 / a.rate / a.pace)
        k.poll(5)

        texts = sorted(ax25ref.frame_to_tnc2(frame) for _, cmd, frame in k.frames if cmd == 0)
        want = sorted('%s>APZWAP:%s<0x0a>' % (s_, i_.rstrip('\n')) for s_, i_ in expected)
        check('KISS client received every WAPR frame exactly once', texts == want,
              '%d received, %d expected' % (len(texts), len(want)))
        if texts != want:
            for t in texts:
                print('   got ', t)
            for t in want:
                print('   want', t)

        gated = [l for l in fis.lines if not l.startswith('#') and not l.startswith('user ')]
        check('nothing heard on WAPR was sent to APRS-IS', not gated, '%d lines' % len(gated))

        # Transmit: one packet WAPR can carry, one it can't.
        good = ax25ref.tnc2_to_frame('N0CALL-1>APDW18:>WAPR live tx<0x0a>')
        bad = ax25ref.tnc2_to_frame('N0CALL-1>APDW18,WIDE1-1:>has a path')
        k.send(good)
        k.send(bad)
        time.sleep(3 + 2.0 * 400 / {'F600': 600.0, 'H150': 150.0}.get(a.profile, 25.0))
    finally:
        k.close()
        p.stop()

    log = p.output()
    check('unrepresentable packet refused with a reason', 'not sent, WAPR has no digipeater path' in log)
    check('decoder kept up', "can't keep up" not in log)
    check('channel announced as experimental', 'EXPERIMENTAL WAPR' in log)

    txwav = os.path.join(a.work, 'tx.wav')
    data = open(txraw, 'rb').read() if os.path.exists(txraw) else b''
    with wave.open(txwav, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(a.rate)
        w.writeframes(data)
    out = subprocess.run([atest, '-W', a.profile, txwav], stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode('latin-1')
    out = re.sub(r'\x1b\[[0-9;]*[A-Za-z]', '', out)
    frames = [l.strip() for l in out.splitlines() if l.startswith('[0] ')]
    check('transmitted audio decodes to exactly the accepted packet',
          frames == ['[0] N0CALL-1>APZWAP:>WAPR live tx<0x0a>'], repr(frames))

    print('%d failure(s)' % len(FAILS))
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
