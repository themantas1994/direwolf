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
 * Module:      wapr_gate.c
 *
 * Purpose:   	Explicit gateway for the experimental WAPR modem.  Nothing
 *		is gated unless the configuration has WAPRGATE rules:
 *
 *		WAPRGATE from to [types]
 *
 *		from	radio channel the frame was heard on.
 *		to	radio channel, or IS for APRS-IS (through the IGate).
 *		types	POS, STATUS, MSG, OBJ, ITEM, WX, TLM, OTHER or ALL
 *			(default), separated by commas.
 *
 *		At least one side of a rule must be a WAPR channel; IS to
 *		WAPR is not supported.
 *
 * Loop prevention:
 *		- A frame a gateway puts on WAPR is WAPR type 2 ("relayed"),
 *		  shown with the path WAPRGW*.  Relayed frames are never gated
 *		  again, by this or any other gateway running this code.
 *		- A frame gated from WAPR to AX.25 is sent as
 *		  SOURCE>APZWAP,MYCALL*,NOGATE:info.  NOGATE keeps every IGate
 *		  (ours, hearing it again, and anyone else's) from putting it
 *		  on APRS-IS: only a WAPRGATE ... IS rule does that.  It also
 *		  keeps digipeaters from spreading it further.
 *		- Nothing with the APZWAP tocall, with one of our own calls
 *		  in the path, from APRS-IS (TCPIP / TCPXX in the path or third
 *		  party format), or from one of our own calls is gated from
 *		  AX.25 to WAPR.
 *		- The same source and information part is gated to the same
 *		  destination at most once in DUP_SECONDS.
 *		- From AX.25, only frames with a good FCS (or FEC) are used,
 *		  as for the digipeater.
 *
 *		Every decision that drops a frame a rule asked for is logged.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <ctype.h>
#include <time.h>

#include "textcolor.h"
#include "audio.h"
#include "config.h"
#include "ax25_pad.h"
#include "tq.h"
#include "igate.h"
#include "dlq.h"
#include "wapr.h"
#include "wapr_gate.h"

#define DUP_SECONDS 60
#define DUP_HISTORY 64

static struct audio_s *save_audio_p;
static struct misc_config_s *save_misc_p;
static int valid[MAX_WAPR_GATES];

static struct {
	unsigned int hash;
	int to;
	time_t when;
} dup[DUP_HISTORY];
static int ndup;


unsigned int wapr_gate_types (const char *list)
{
	static const struct { const char *name; unsigned int bit; } names[] = {
		{ "POS", WAPR_GT_POS }, { "STATUS", WAPR_GT_STATUS }, { "MSG", WAPR_GT_MSG },
		{ "OBJ", WAPR_GT_OBJ }, { "ITEM", WAPR_GT_ITEM }, { "WX", WAPR_GT_WX },
		{ "TLM", WAPR_GT_TLM }, { "OTHER", WAPR_GT_OTHER }, { "ALL", WAPR_GT_ALL } };
	char buf[100];
	unsigned int bits = 0;

	strlcpy (buf, list, sizeof(buf));
	for (char *tok = strtok(buf, ","); tok != NULL; tok = strtok(NULL, ",")) {
	  unsigned int b = 0;
	  for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); i++) {
	    if (strcasecmp(tok, names[i].name) == 0) b = names[i].bit;
	  }
	  if (b == 0) return (0);
	  bits |= b;
	}
	return (bits);
}


static unsigned int type_class (const unsigned char *info, int len)
{
	if (len < 1) return (WAPR_GT_OTHER);
	switch (info[0]) {
	  case '!': case '=': case '/': case '@': case '`': case '\'': return (WAPR_GT_POS);
	  case '>': return (WAPR_GT_STATUS);
	  case ':': return (WAPR_GT_MSG);
	  case ';': return (WAPR_GT_OBJ);
	  case ')': return (WAPR_GT_ITEM);
	  case '_': return (WAPR_GT_WX);
	  case 'T': return (WAPR_GT_TLM);
	}
	return (WAPR_GT_OTHER);
}


static int is_wapr (int chan)
{
	return (chan >= 0 && chan < MAX_RADIO_CHANS && save_audio_p->chan_medium[chan] == MEDIUM_RADIO &&
		save_audio_p->achan[chan].modem_type == MODEM_WAPR);
}

static const char *dest_name (int to, char *buf, int len)
{
	if (to < 0) snprintf (buf, len, "APRS-IS");
	else snprintf (buf, len, "channel %d", to);
	return (buf);
}


void wapr_gate_init (struct audio_s *pa, struct misc_config_s *mc)
{
	save_audio_p = pa;
	save_misc_p = mc;
	ndup = 0;

	for (int i = 0; i < mc->num_wapr_gates; i++) {
	  struct wapr_gate_s *g = &mc->wapr_gate[i];
	  char d[20];
	  valid[i] = 0;
	  if (g->from < 0 || g->from >= MAX_RADIO_CHANS || pa->chan_medium[g->from] != MEDIUM_RADIO ||
	      (g->to >= 0 && (g->to >= MAX_RADIO_CHANS || pa->chan_medium[g->to] != MEDIUM_RADIO)) || g->to == g->from) {
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("WAPRGATE %d to %s: both must be different radio channels (or IS).  Rule ignored.\n",
			g->from, dest_name(g->to, d, sizeof(d)));
	    continue;
	  }
	  if (! is_wapr(g->from) && ! is_wapr(g->to)) {
	    text_color_set(DW_COLOR_ERROR);
	    dw_printf ("WAPRGATE %d to %s: neither side is a MODEM WAPR channel.  Rule ignored.\n",
			g->from, dest_name(g->to, d, sizeof(d)));
	    continue;
	  }
	  valid[i] = 1;
	  text_color_set(DW_COLOR_INFO);
	  dw_printf ("EXPERIMENTAL WAPR gateway: channel %d (%s) to %s (%s), types 0x%02x.\n",
		g->from, is_wapr(g->from) ? "WAPR" : "AX.25", dest_name(g->to, d, sizeof(d)),
		g->to < 0 ? "IGate" : is_wapr(g->to) ? "WAPR" : "AX.25", g->types);
	}
}


static unsigned int hash_frame (const char *src, const unsigned char *info, int len)
{
	unsigned int h = 2166136261u;		/* FNV-1a */
	for (const char *p = src; *p; p++) h = (h ^ (unsigned char)*p) * 16777619u;
	h = (h ^ '>') * 16777619u;
	for (int i = 0; i < len; i++) h = (h ^ info[i]) * 16777619u;
	return (h);
}

/* 1 if already gated to this destination recently; otherwise remember it and return 0. */

static int seen_recently (unsigned int h, int to)
{
	time_t now = time(NULL);
	for (int i = 0; i < ndup && i < DUP_HISTORY; i++) {
	  if (dup[i].hash == h && dup[i].to == to && now - dup[i].when < DUP_SECONDS) return (1);
	}
	int k = ndup % DUP_HISTORY;
	dup[k].hash = h;
	dup[k].to = to;
	dup[k].when = now;
	ndup++;
	return (0);
}

static int is_own_call (const char *addr)
{
	for (int c = 0; c < MAX_TOTAL_CHANS; c++) {
	  if (save_audio_p->mycall[c][0] != '\0' && strcmp(save_audio_p->mycall[c], addr) == 0) return (1);
	}
	return (0);
}

static void drop (const char *why, const char *src, int from, int to)
{
	char d[20];
	text_color_set(DW_COLOR_INFO);
	dw_printf ("[WAPR gate %d>%s] not gated, %s: %s\n", from, dest_name(to, d, sizeof(d)), why, src);
}


void wapr_gate_rec (int chan, int subchan, packet_t pp, fec_type_t fec_type, retry_t retries)
{
	char src[AX25_MAX_ADDR_LEN], dst[AX25_MAX_ADDR_LEN], addr[AX25_MAX_ADDR_LEN];
	unsigned char *info;

	if (save_misc_p == NULL || save_misc_p->num_wapr_gates == 0 || subchan < 0 || chan < 0 || chan >= MAX_RADIO_CHANS) return;
	if (! ax25_is_aprs(pp)) return;

	int from_wapr = fec_type == fec_type_wapr;
	if (! from_wapr && ! (retries == RETRY_NONE || fec_type == fec_type_fx25 || fec_type == fec_type_il2p)) return;

	int len = ax25_get_info (pp, &info);
	unsigned int tclass = type_class (info, len);
	ax25_get_addr_with_ssid (pp, AX25_SOURCE, src);
	ax25_get_addr_no_ssid (pp, AX25_DESTINATION, dst);

	int relayed = 0, own_in_path = 0, from_is = 0;
	for (int r = 0; r < ax25_get_num_repeaters(pp); r++) {
	  ax25_get_addr_with_ssid (pp, AX25_REPEATER_1 + r, addr);
	  if (strcmp(addr, WAPR_RELAY_MARK) == 0) relayed = 1;
	  if (is_own_call(addr)) own_in_path = 1;
	  if (strcmp(addr, "TCPIP") == 0 || strcmp(addr, "TCPXX") == 0) from_is = 1;
	}

	for (int i = 0; i < save_misc_p->num_wapr_gates; i++) {
	  struct wapr_gate_s *g = &save_misc_p->wapr_gate[i];
	  if (! valid[i] || g->from != chan || ! (g->types & tclass)) continue;

	  if (from_wapr && relayed) { drop ("relayed by a gateway already", src, chan, g->to); continue; }
	  if (! from_wapr) {
	    if (strcmp(dst, WAPR_BROADCAST_TOCALL) == 0) { drop ("came from WAPR", src, chan, g->to); continue; }
	    if (own_in_path) { drop ("our call is in the path", src, chan, g->to); continue; }
	    if (is_own_call(src)) { drop ("our own transmission", src, chan, g->to); continue; }
	    if (from_is || (len > 0 && info[0] == '}')) { drop ("from APRS-IS (third party format or TCPIP)", src, chan, g->to); continue; }
	    if (len > WAPR_PAYLOAD_AREA) { drop ("information part longer than 32 bytes", src, chan, g->to); continue; }
	  }
	  if (seen_recently(hash_frame(src, info, len), g->to)) { drop ("duplicate", src, chan, g->to); continue; }

	  if (g->to < 0) {
	    igate_send_rec_packet (chan, pp);	/* IGate applies its own rules and q construct */
	    continue;
	  }

	  char text[120];
	  if (is_wapr(g->to)) {
	    snprintf (text, sizeof(text), "%s>%s,%s:", src, WAPR_BROADCAST_TOCALL, WAPR_RELAY_MARK);
	  }
	  else {
	    if (save_audio_p->mycall[g->to][0] == '\0' || strcasecmp(save_audio_p->mycall[g->to], "NOCALL") == 0) {
	      drop ("no MYCALL for the AX.25 channel", src, chan, g->to);
	      continue;
	    }
	    snprintf (text, sizeof(text), "%s>%s,%s*,NOGATE:", src, WAPR_BROADCAST_TOCALL, save_audio_p->mycall[g->to]);
	  }
	  packet_t np = ax25_from_text (text, 1);
	  if (np == NULL) { drop ("address not valid on the other side", src, chan, g->to); continue; }
	  ax25_set_info (np, info, len);
	  tq_append (g->to, TQ_PRIO_1_LO, np);
	}
}
