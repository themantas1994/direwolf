
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
 * Module:      l2send_test.c
 *
 * Purpose:   	Unit test for the layer 2 transmit bit stream: hdlc_send.c
 *		and fx25_send.c together.
 *
 * Description:	An AX.25 frame too long for FX.25 is sent as plain AX.25.
 *		When it follows an FX.25 frame in the same transmission, its
 *		opening flag comes right after the FX.25 check bytes.  The two
 *		modules used to keep separate NRZI states so the first bit after
 *		switching from one to the other was inverted about half the time,
 *		destroying that only flag and the frame with it.
 *
 *		Here the bits that would go to the modulator are captured, NRZI
 *		decoded and searched for HDLC frames with a good FCS, the way any
 *		AX.25 receiver would.  Every frame sent must be found.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <assert.h>

#include "audio.h"
#include "ax25_pad.h"
#include "hdlc_send.h"
#include "fx25.h"
#include "il2p.h"
#include "wapr_tx.h"
#include "fcs_calc.h"
#include "gen_tone.h"


/* Stand in for the modulator: collect the bits. */

#define MAXBITS 2000000
static unsigned char line[MAXBITS];
static int nline = 0;

void tone_gen_put_bit (int chan, int dat)
{
	(void)chan;
	assert (nline < MAXBITS);
	line[nline++] = dat & 1;
}

int tone_gen_last_bit (int chan)
{
	(void)chan;
	return (nline > 0 ? line[nline-1] : 0);
}

int audio_flush (int a)
{
	(void)a;
	return (0);
}

void gen_tone_put_quiet_ms (int chan, int time_ms)	/* Only used for EAS. */
{
	(void)chan; (void)time_ms;
}

/* Not used here.  Avoids linking all of IL2P. */

int il2p_send_frame (int chan, packet_t pp, int max_fec, int polarity)
{
	(void)chan; (void)pp; (void)max_fec; (void)polarity;
	return (-1);
}

/* Not used here either: no WAPR channel in this test. */

int wapr_send_frame (int chan, packet_t pp, struct audio_s *pa)
{
	(void)chan; (void)pp; (void)pa;
	return (-1);
}

int wapr_send_silence (int chan, int nsym, struct audio_s *pa)
{
	(void)chan; (void)pa;
	return (nsym);
}


/*
 * A plain AX.25 receiver: NRZI decode, find flags, remove stuffed bits,
 * keep frames with a good FCS.
 */

#define MAXFOUND 100
static unsigned char found[MAXFOUND][AX25_MAX_PACKET_LEN+2];
static int found_len[MAXFOUND];
static int nfound = 0;

static void receive (void)
{
	int i;
	int prev = 0;
	unsigned int pat = 0;
	unsigned char frame[AX25_MAX_PACKET_LEN+3];
	int nbits = 0;
	int ones = 0;
	int in_frame = 0;
	int acc = 0;

	nfound = 0;
	for (i = 0; i < nline; i++) {
	  int b = (line[i] == prev);		/* NRZI: no change = 1. */
	  prev = line[i];
	  pat = ((pat >> 1) | (b << 7)) & 0xff;

	  if (pat == 0x7e) {			/* Flag.  End of any frame in progress. */
	    if (in_frame && nbits % 8 == 7 && nbits / 8 >= AX25_MIN_PACKET_LEN + 2) {
	      int flen = nbits / 8;
	      if (fcs_calc(frame, flen - 2) == (frame[flen-2] | (frame[flen-1] << 8)) && nfound < MAXFOUND) {
	        memcpy (found[nfound], frame, flen - 2);
	        found_len[nfound] = flen - 2;
	        nfound++;
	      }
	    }
	    in_frame = 1;
	    nbits = 0;
	    ones = 0;
	    acc = 0;
	    continue;
	  }
	  if ( ! in_frame) continue;

	  if (b) {
	    ones++;
	    if (ones > 6) {			/* Abort. */
	      in_frame = 0;
	      continue;
	    }
	  }
	  else {
	    if (ones == 5) {			/* Stuffed bit. */
	      ones = 0;
	      continue;
	    }
	    ones = 0;
	  }
	  if (nbits / 8 >= (int)sizeof(frame)) {
	    in_frame = 0;
	    continue;
	  }
	  acc |= b << (nbits % 8);
	  if (nbits % 8 == 7) {
	    frame[nbits / 8] = acc;
	    acc = 0;
	  }
	  nbits++;
	}
}

static int was_received (packet_t pp)
{
	unsigned char fbuf[AX25_MAX_PACKET_LEN+2];
	int flen = ax25_pack (pp, fbuf);

	for (int k = 0; k < nfound; k++) {
	  if (found_len[k] == flen && memcmp(found[k], fbuf, flen) == 0) {
	    return (1);
	  }
	}
	return (0);
}


int main (int argc, char *argv[])
{
	(void)argc; (void)argv;
	static struct audio_s audio_config;
	int errors = 0;
	char text[600];

	memset (&audio_config, 0, sizeof(audio_config));
	audio_config.chan_medium[0] = MEDIUM_RADIO;
	audio_config.achan[0].layer2_xmit = LAYER2_FX25;
	audio_config.achan[0].fx25_strength = 16;
	fx25_init (0);

	/* Long frame: too long for FX.25 so it is sent as plain AX.25. */

	char longinfo[300];
	memset (longinfo, 'L', sizeof(longinfo) - 1);
	longinfo[sizeof(longinfo) - 1] = '\0';
	snprintf (text, sizeof(text), "W1ABC>APRS:%s", longinfo);
	packet_t plong = ax25_from_text (text, 1);
	assert (plong != NULL);

	/* Short frames of different lengths and content, so the NRZI level */
	/* before the long frame is different from one test to the next. */

	for (int t = 0; t < 12; t++) {

	  nline = 0;
	  snprintf (text, sizeof(text), "W1ABC>APRS:>short %d %.*s", t, t * 3, "0123456789abcdefghijklmnopqrstuvwxyz0123456789");
	  packet_t pshort = ax25_from_text (text, 1);
	  assert (pshort != NULL);

	  layer2_preamble_postamble (0, 16, 0, &audio_config);
	  layer2_send_frame (0, pshort, 0, &audio_config);	/* FX.25 */
	  layer2_send_frame (0, plong, 0, &audio_config);	/* Plain AX.25, right after it. */
	  layer2_send_frame (0, pshort, 0, &audio_config);	/* FX.25 again. */
	  layer2_preamble_postamble (0, 4, 0, &audio_config);

	  receive ();

	  /* The FX.25 frames also contain the AX.25 frame, so a plain receiver */
	  /* finds them too.  The long one is only there as plain AX.25. */

	  if ( ! was_received (pshort)) {
	    printf ("Test %d: short frame (FX.25) not received.\n", t);
	    errors++;
	  }
	  if ( ! was_received (plong)) {
	    printf ("Test %d: long frame (plain AX.25 after FX.25) not received.\n", t);
	    errors++;
	  }
	  ax25_delete (pshort);
	}

	/* And plain AX.25 only: two frames in one transmission. */

	audio_config.achan[0].layer2_xmit = LAYER2_AX25;
	nline = 0;
	layer2_preamble_postamble (0, 16, 0, &audio_config);
	layer2_send_frame (0, plong, 0, &audio_config);
	layer2_send_frame (0, plong, 0, &audio_config);
	layer2_preamble_postamble (0, 4, 0, &audio_config);
	receive ();
	if (nfound != 2 || ! was_received (plong)) {
	  printf ("Plain AX.25: expected 2 frames, found %d.\n", nfound);
	  errors++;
	}

	ax25_delete (plong);

	if (errors != 0) {
	  printf ("\nl2send_test: %d errors.\n", errors);
	  exit (EXIT_FAILURE);
	}
	printf ("\nl2send_test: all frames received.\n");
	exit (EXIT_SUCCESS);
}
