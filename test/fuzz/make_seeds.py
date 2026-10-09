#!/usr/bin/env python3
"""Write a seed corpus for fuzz_rx: typical APRS packets as text, raw AX.25 and KISS."""
import os, sys

PACKETS = [
    "N0CALL>APDW18,WIDE1-1,WIDE2-1:!4237.14N/07120.83W#PHG7140 test",
    "N0CALL-9>T2SP0W,WIDE1-1:`c51!f?>/]\"4W}=",
    "WB7DRJ>S6QRTU,VEGAS,WIDE1*,WIDE2-1:`,[5l#\">/'\"<D}|%/%6'N|!wx>!|3",
    "N0CALL>APRS::KB1ABC-7 :Hello there{12",
    "N0CALL>APRS::KB1ABC-7 :ack12",
    "N0CALL>APRS:;LEADER   *092345z4903.50N/07201.75W>088/036",
    "N0CALL>APRS:)AID #2!4903.50N/07201.75WA",
    "N0CALL>APRS:T#005,199,000,255,073,123,01101001",
    "N0CALL>APRS::N0CALL   :PARM.Battery,Btemp,ATemp,Pres,Alt,Camra,Chut,Sun,10m,ATV",
    "N0CALL>APRS:_10090556c220s004g005t077r000p000P000h50b09900wRSW",
    "N0CALL>APRS:@092345z4903.50N/07201.75W_220/004g005t077r000p000P000h50b09900",
    "N0CALL>APRS:>Status text here",
    "N0CALL>APRS:=/5L!!<*e7>7P[",
    "N0CALL>APRS:!4903.50N/07201.75W-DFS2360 T123 tone 100.0",
    "N0CALL>APRS:}W1AW>APRS,TCPIP,N0CALL*:!4903.50N/07201.75W-third party",
    "N0CALL>APRS:?APRS? 34.02 -117.15 0200",
    "N0CALL>APRS:$GPRMC,063909,A,3349.4302,N,11700.3721,W,43.022,89.3,291099,13.1,E*55",
    "N0CALL>APRS:{DA!AIVDM,1,1,,A,15M67FC000G?ufbE`FepT@3n00Sa,0*5F",
    "N0CALL>APRS:/092345z4903.50N/07201.75W>088/036/A=001234 Comment",
]

def addr(c, last):
    call, _, ssid = c.rstrip('*').partition('-')
    b = bytes(ord(x) << 1 for x in call.ljust(6)[:6])
    return b + bytes([0x60 | (int(ssid or 0) << 1) | (0x80 if c.endswith('*') else 0) | last])

def raw(p):
    head, _, info = p.partition(':')
    src, _, rest = head.partition('>')
    calls = rest.split(',')
    calls = [calls[0], src] + calls[1:]
    return b''.join(addr(c, i == len(calls) - 1) for i, c in enumerate(calls)) + b'\x03\xf0' + info.encode('latin-1')

def kiss(f):
    return b'\xc0\x00' + f.replace(b'\xdb', b'\xdb\xdd').replace(b'\xc0', b'\xdb\xdc') + b'\xc0'

out = sys.argv[1] if len(sys.argv) > 1 else 'corpus'
os.makedirs(out, exist_ok=True)
for i, p in enumerate(PACKETS):
    r = raw(p)
    for tag, data in (('t', b'\x02' + p.encode('latin-1')), ('r', b'\x00' + r), ('k', b'\x01' + kiss(r))):
        with open(os.path.join(out, '%s%02d' % (tag, i)), 'wb') as f:
            f.write(data)
