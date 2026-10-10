#!/usr/bin/env python3
"""End to end test of the WAPRGATE rules and their loop prevention, with a real direwolf.

    python3 wapr_gate_test.py BUILD_DIR WORK_DIR [--pace 2]

One direwolf, stereo audio over UDP: channel 0 is AX.25 1200 bd (left), channel 1 is
MODEM WAPR H150 (right).  Rules:

    WAPRGATE 1 0                     WAPR heard -> AX.25 transmit, all types
    WAPRGATE 0 1 POS,STATUS,MSG,OTHER  AX.25 heard -> WAPR transmit, not OBJ / ITEM / WX / TLM
    WAPRGATE 1 IS                    WAPR heard -> APRS-IS (fake server here)

The received audio holds frames that must be gated and frames that must not (wrong
type, already from WAPR, our call in the path, third party, too long, duplicate, our
own call, relayed by a gateway).  The transmit audio of both channels is decoded
again and compared with what the rules allow.  Linux only (ALSA file plugin).
"""

import argparse
import os
import re
import socket
import subprocess
import sys
import time
import wave

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'compat'))
sys.path.insert(0, HERE)

import dw_session as D          # noqa: E402
from wapr_live import FakeIS    # noqa: E402

FAILS = []
RATE = 48000


def check(name, cond, detail=''):
    print('%-4s %s %s' % ('ok' if cond else 'FAIL', name, detail))
    if not cond:
        FAILS.append(name)


def gen(build, args, msgs, path):
    mfile = path + '.txt'
    with open(mfile, 'w', newline='\n') as f:
        f.write(''.join(m + '\n' for m in msgs))
    subprocess.run([os.path.join(build, 'src', 'gen_packets')] + args + ['-r', str(RATE), '-o', path, mfile],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    with wave.open(path) as w:
        return np.frombuffer(w.readframes(w.getnframes()), dtype='<i2')


def decode(build, args, samples, path):
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(samples.astype('<i2').tobytes())
    out = subprocess.run([os.path.join(build, 'src', 'atest')] + args + [path],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT).stdout.decode('latin-1')
    out = re.sub(r'\x1b\[[0-9;]*[A-Za-z]', '', out)
    return [l[4:].strip() for l in out.splitlines() if l.startswith('[0] ')]


def stream(udp, raw, pace):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    chunk = 1024
    t0 = time.time()
    for i in range(0, len(raw), chunk):
        s.sendto(raw[i:i + chunk], ('127.0.0.1', udp))
        target = t0 + (i + chunk) / 4 / RATE / pace
        while time.time() < target:
            time.sleep(0.0005)


def run_once(a, raw, name):
    """Run direwolf on stereo audio, then on its own transmissions (echo), in one process.

    Returns two dicts, for the original audio and for the echo, each with the AX.25 and
    WAPR frames transmitted, the APRS-IS lines, and the log; the log is shared."""
    fis = FakeIS()
    fis.start()
    udp = D.free_port()
    kp, agw = D.free_port(), D.free_port()
    os.makedirs('/tmp/dwt', exist_ok=True)
    txraw = '/tmp/dwt/waprgate'     # direwolf truncates device names at 29 characters
    if os.path.exists(txraw):
        os.remove(txraw)
    conf = ['ADEVICE udp:%d file:%s' % (udp, txraw), 'ACHANNELS 2', 'ARATE %d' % RATE,
            'CHANNEL 0', 'MYCALL GW0', 'MODEM 1200', 'TXDELAY 20', 'TXTAIL 5',
            'CHANNEL 1', 'MYCALL GW0-1', 'MODEM WAPR H150', 'TXDELAY 20', 'TXTAIL 5',
            'WAPRGATE 1 0', 'WAPRGATE 0 1 POS,STATUS,MSG,OTHER', 'WAPRGATE 1 IS',
            'KISSPORT %d' % kp, 'AGWPORT %d' % agw,
            'IGSERVER 127.0.0.1:%d' % fis.port, 'IGLOGIN GW0 13023']
    p = D.Direwolf(os.path.join(a.build, 'src', 'direwolf'), conf, a.work, name=name)
    try:
        D.wait_port(kp).close()
        t0 = time.time()
        while not fis.connected and time.time() - t0 < 30:
            time.sleep(0.2)
        time.sleep(8)           # igate.c sends nothing for 7 s after logging in
        stream(udp, raw, a.pace)
        time.sleep(15)          # let the transmit queue empty (WAPR frames are 2.7 s)
        first = np.fromfile(txraw, dtype='<i2') if os.path.exists(txraw) else np.zeros(0, dtype='<i2')
        first = first[:len(first) // 2 * 2].reshape(-1, 2)
        n_is = len(fis.lines)
        # Echo: everything transmitted so far is heard again on the same channels,
        # as if repeated by a digipeater.
        echo = np.zeros((len(first) + RATE * 6, 2), dtype='<i2')
        echo[:len(first)] = first
        stream(udp, echo.reshape(-1).tobytes(), a.pace)
        time.sleep(15)
    finally:
        p.stop()

    log = p.output()
    data = np.fromfile(txraw, dtype='<i2') if os.path.exists(txraw) else np.zeros(0, dtype='<i2')
    data = data[:len(data) // 2 * 2].reshape(-1, 2)
    out = []
    for part, lines, tag in ((data[:len(first)], fis.lines[:n_is], 'first'), (data[len(first):], fis.lines[n_is:], 'echo')):
        out.append(dict(
            ax25=decode(a.build, ['-B', '1200'], part[:, 0], os.path.join(a.work, '%s_%s_left.wav' % (name, tag))),
            wapr=decode(a.build, ['-W', 'H150'], part[:, 1], os.path.join(a.work, '%s_%s_right.wav' % (name, tag))),
            is_lines=[l for l in lines if not l.startswith('#') and not l.startswith('user ')]))
    return out[0], out[1], log


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('build')
    ap.add_argument('work')
    ap.add_argument('--pace', type=float, default=2.0)
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)

    ax25_msgs = [
        'K1AAA>APRS:!4237.14N/07120.83W-ax25 pos',            # gated to WAPR
        'K1BBB>APRS:;OBJECT   *111111z4237.14N/07120.83W-',     # OBJ: not in the rule
        'K1CCC>APZWAP,GW9*:>came from wapr',                    # tocall APZWAP
        'K1DDD>APRS,GW0*:>our call in path',                    # our call in path
        'K1EEE>APRS:}W1X>APRS,TCPIP,K1EEE*:>third',             # third party
        'K1FFF>APRS:>this one is far too long for one WAPR frame',  # > 32 bytes
        'K1AAA>APRS:!4237.14N/07120.83W-ax25 pos',            # duplicate of the first
        'GW0>APRS:>own transmission',                           # our own call
    ]
    wapr_msgs = [
        'W1AAA>APDW18:!4237.14N/07120.83W-wapr pos',          # gated to AX.25 and IS
        'W1BBB>APDW18,WAPRGW:>relayed already',                # type 2: never gated
        'W1CCC>APDW18::K1AAA    :hi{01',                      # message: gated
    ]
    left = gen(a.build, ['-B', '1200'], ax25_msgs, os.path.join(a.work, 'left.wav'))
    right = gen(a.build, ['-W', 'H150'], wapr_msgs, os.path.join(a.work, 'right.wav'))
    # The WAPR frames start 8 s later: the gateway transmits K1AAA on the WAPR channel
    # first, and a half duplex radio hears nothing while it transmits.
    delay = RATE * 8
    n = max(len(left), delay + len(right)) + RATE * 6
    stereo = np.zeros((n, 2), dtype='<i2')
    stereo[:len(left), 0] = left
    stereo[delay:delay + len(right), 1] = right
    raw = stereo.reshape(-1).tobytes()

    first, echo, log = run_once(a, raw, 'gate')
    tx_ax25, tx_wapr, is_lines = first['ax25'], first['wapr'], first['is_lines']

    check('WAPR -> AX.25: exactly the two allowed frames, with our call and NOGATE',
          sorted(tx_ax25) == sorted(['W1AAA>APZWAP,GW0*,NOGATE:!4237.14N/07120.83W-wapr pos<0x0a>',
                                     'W1CCC>APZWAP,GW0*,NOGATE::K1AAA    :hi{01<0x0a>']), repr(tx_ax25))
    check('AX.25 -> WAPR: exactly one frame, relayed type, no duplicate',
          tx_wapr == ['K1AAA>APZWAP,WAPRGW*:!4237.14N/07120.83W-ax25 pos<0x0a>'], repr(tx_wapr))
    w_is = [l for l in is_lines if l.startswith('W1')]
    check('WAPR -> APRS-IS: W1AAA and W1CCC once each, W1BBB (relayed) never',
          sorted(l.split('>')[0] for l in w_is) == ['W1AAA', 'W1CCC'], repr(w_is))
    for reason in ('came from WAPR', 'our call is in the path', 'from APRS-IS',
                   'information part longer than 32 bytes', 'duplicate', 'our own transmission',
                   'relayed by a gateway already'):
        check('refusal logged: ' + reason, reason in log)
    check('gateway rules announced', 'EXPERIMENTAL WAPR gateway' in log)

    # Echo, same process: what the gateway sent is heard again on its own channels.
    # Nothing may be transmitted again and nothing may reach APRS-IS again (the
    # existing RF IGate on channel 0 hears W1AAA / W1CCC as AX.25 frames now; NOGATE
    # in their path must stop it, because only WAPRGATE ... IS may put WAPR on APRS-IS).
    check('echo: nothing transmitted again', echo['ax25'] == [] and echo['wapr'] == [], repr(echo['ax25'] + echo['wapr']))
    check('echo: nothing reaches APRS-IS again', echo['is_lines'] == [], repr(echo['is_lines']))
    check('echo: gated frames recognised (relayed / from WAPR / our call)',
          log.count('relayed by a gateway already') >= 2 and ('came from WAPR' in log))

    print('%d failure(s)' % len(FAILS))
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
