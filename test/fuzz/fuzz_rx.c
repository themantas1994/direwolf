// libFuzzer harness for received packet parsing.
//
// The first input byte selects how the rest is interpreted:
//   0 - raw AX.25 frame (without FCS) -> ax25_from_frame
//   1 - KISS frame -> kiss_unwrap -> ax25_from_frame
//   2 - monitor format text, e.g. "SRC>DST,PATH:info" -> ax25_from_text
// The packet then goes through the address formatting, frame type and
// packing functions, and decode_aprs if it is APRS.
//
// See README.md for building and running.  Not part of the normal build.
#include "direwolf.h"
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include "ax25_pad.h"
#include "decode_aprs.h"
#include "kiss_frame.h"
#include "textcolor.h"
#include "deviceid.h"

int LLVMFuzzerTestOneInput (const uint8_t *data, size_t size)
{
	static int init = 0;
	if (!init) { text_color_init(0); deviceid_init(); init = 1; }
	if (size < 1 || size > 2*AX25_MAX_PACKET_LEN) return 0;

	unsigned char buf[2*AX25_MAX_PACKET_LEN+2];
	unsigned char out[2*AX25_MAX_PACKET_LEN+2];
	int sel = data[0] % 3;
	int n = (int)size - 1;
	memcpy (buf, data + 1, n);
	packet_t pp = NULL;
	alevel_t alevel; memset (&alevel, 0, sizeof(alevel));

	if (sel == 0) {
	  if (n >= AX25_MIN_PACKET_LEN && n <= AX25_MAX_PACKET_LEN) pp = ax25_from_frame (buf, n, alevel);
	}
	else if (sel == 1) {
	  int m = kiss_unwrap (buf, n, out);
	  if (m >= 1 + AX25_MIN_PACKET_LEN && m <= AX25_MAX_PACKET_LEN + 1) pp = ax25_from_frame (out + 1, m - 1, alevel);
	}
	else {
	  buf[n] = '\0';
	  pp = ax25_from_text ((char*)buf, 0);
	}
	if (pp == NULL) return 0;

	char addrs[AX25_MAX_ADDRS*AX25_MAX_ADDR_LEN];
	ax25_format_addrs (pp, addrs);
	cmdres_t cr; char desc[80]; int pf; int nr, ns;
	(void) ax25_frame_type (pp, &cr, desc, &pf, &nr, &ns);
	unsigned char fbuf[AX25_MAX_PACKET_LEN];
	(void) ax25_pack (pp, fbuf);
	(void) ax25_dedupe_crc (pp);
	(void) ax25_m_m_crc (pp);
	if (ax25_is_aprs (pp)) {
	  decode_aprs_t A;
	  decode_aprs (&A, pp, 1, NULL);
	}
	ax25_delete (pp);
	return 0;
}
