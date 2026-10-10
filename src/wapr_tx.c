//
//    This file is part of Dire Wolf, an amateur radio packet TNC.
//
//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 2 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program.  If not, see <http://www.gnu.org/licenses/>.
//


/*------------------------------------------------------------------
 *
 * Module:      wapr_tx.c
 *
 * Purpose:   	Transmit side of the experimental WAPR modem, used by
 *		hdlc_send.c for channels configured with "MODEM WAPR profile".
 *
 * Description:	Packets from the transmit queue (clients, beacons) are
 *		mapped to a WAPR frame or rejected with a message; nothing
 *		on a WAPR channel is ever sent as AX.25.
 *
 *		Transmit timing in xmit.c counts "bits" at the channel's
 *		baud; for a WAPR channel that is the symbol rate, so the
 *		functions here return symbol counts and TXDELAY / TXTAIL
 *		become silence of the same duration.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <math.h>

#include "textcolor.h"
#include "audio.h"
#include "ax25_pad.h"
#include "gen_tone.h"
#include "wapr.h"
#include "wapr_tx.h"


int wapr_frame_from_packet (packet_t pp, wapr_frame_t *f, char *why, int whylen)
{
	cmdres_t cr;
	char desc[80];
	int pf, nr, ns;
	unsigned char *info;
	char addr[AX25_MAX_ADDR_LEN];

	memset (f, 0, sizeof(*f));
	if (ax25_frame_type(pp, &cr, desc, &pf, &nr, &ns) != frame_type_U_UI || ax25_get_pid(pp) != 0xF0) {
	  snprintf (why, whylen, "only UI frames with PID F0 can be sent");
	  return (WAPR_ERR_TYPE);
	}
	int relayed = 0;
	if (ax25_get_num_repeaters(pp) == 1) {
	  ax25_get_addr_with_ssid (pp, AX25_REPEATER_1, addr);
	  relayed = strcmp(addr, WAPR_RELAY_MARK) == 0;	/* from a gateway (wapr_gate.c) */
	}
	if (ax25_get_num_repeaters(pp) != 0 && ! relayed) {
	  snprintf (why, whylen, "WAPR has no digipeater path");
	  return (WAPR_ERR_ADDRESS);
	}
	int len = ax25_get_info (pp, &info);
	if (len > WAPR_PAYLOAD_AREA) {
	  snprintf (why, whylen, "information part is %d bytes, at most %d fit", len, WAPR_PAYLOAD_AREA);
	  return (WAPR_ERR_LENGTH);
	}
	if (len > 0 && (info[0] == '`' || info[0] == '\'' || info[0] == 0x1c || info[0] == 0x1d)) {
	  snprintf (why, whylen, "MIC-E keeps data in the destination field, which WAPR does not carry");
	  return (WAPR_ERR_TYPE);
	}
	ax25_get_addr_with_ssid (pp, AX25_SOURCE, addr);
	strlcpy (f->source, addr, sizeof(f->source));
	f->dest[0] = '\0';		/* APRS: the destination is a tocall, not an address */
	f->type = relayed ? WAPR_TYPE_APRS_RELAYED : WAPR_TYPE_APRS;
	f->seq = 0;
	f->len = len;
	memcpy (f->payload, info, len);

	unsigned char tmp[WAPR_INFO_BYTES];
	int e = wapr_frame_pack (f, tmp);
	if (e != WAPR_OK) {
	  snprintf (why, whylen, "%s (source %s)", wapr_strerror(e), addr);
	}
	return (e);
}


int wapr_send_silence (int chan, int nsym, struct audio_s *pa)
{
	int a = ACHAN2ADEV(chan);
	const wapr_profile_t *p = wapr_profile_find (pa->achan[chan].wapr_profile);
	if (p == NULL) return (0);
	int n = (int)round(nsym * pa->adev[a].samples_per_sec / p->baud);
	for (int i = 0; i < n; i++) {
	  gen_tone_put_sample (chan, a, 0);
	}
	return (nsym);
}


int wapr_send_frame (int chan, packet_t pp, struct audio_s *pa)
{
	static int seq = 0;		/* message identity: not used by the receiver yet (stage 4) */
	int a = ACHAN2ADEV(chan);
	const wapr_profile_t *p = wapr_profile_find (pa->achan[chan].wapr_profile);
	wapr_frame_t f;
	char why[120];
	unsigned char info[WAPR_INFO_BYTES];
	unsigned char syms[WAPR_MAX_SYMBOLS];

	if (p == NULL) return (-1);
	if (wapr_frame_from_packet(pp, &f, why, sizeof(why)) != WAPR_OK) {
	  char text[AX25_MAX_ADDRS*AX25_MAX_ADDR_LEN];
	  ax25_format_addrs (pp, text);
	  text_color_set(DW_COLOR_ERROR);
	  dw_printf ("WAPR channel %d: not sent, %s: %s\n", chan, why, text);
	  return (-1);
	}
	f.seq = seq;
	seq = (seq + 1) & 1023;
	wapr_frame_pack (&f, info);
	int nsym = wapr_encode (p, info, syms);

	int fs = pa->adev[a].samples_per_sec;
	int n = wapr_samples_needed (p, fs);
	float *x = malloc (sizeof(float) * n);
	if (x == NULL) return (-1);
	n = wapr_modulate (p, syms, nsym, fs, (float)gen_tone_amplitude(), x, n);
	for (int i = 0; i < n; i++) {
	  gen_tone_put_sample (chan, a, (int)lrintf(x[i]));
	}
	free (x);
	return (nsym);
}
