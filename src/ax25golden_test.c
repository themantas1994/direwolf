
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
 * Module:      ax25golden_test.c
 *
 * Purpose:   	Check AX.25 frame encoding and decoding against test vectors
 *		made independently of Dire Wolf's code, from the AX.25 v2.2
 *		specification (test/compat/make_golden.py, ax25ref.py).
 *
 *		- Monitor text to frame bytes (ax25_from_text, ax25_pack).
 *		- FCS (fcs_calc), CRC-16/X.25.
 *		- Frame bytes back to addresses and information part.
 *		- Frame type, command/response, P/F, N(R), N(S), modulo 8
 *		  and 128 (ax25_frame_type).
 *		- Rejection of frames with a bad length or address field.
 *
 * Usage:	ax25goldentest [ax25_vectors.txt]
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <ctype.h>

#include "ax25_pad.h"
#include "fcs_calc.h"
#include "textcolor.h"


static int errors = 0;

#define MAXLINE 10000


static int unhex (const char *s, unsigned char *out, int max)
{
	int n = 0;
	while (isxdigit((unsigned char)s[0]) && isxdigit((unsigned char)s[1]) && n < max) {
	  char b[3] = { s[0], s[1], '\0' };
	  out[n++] = strtoul (b, NULL, 16);
	  s += 2;
	}
	return (n);
}

static void fail (int lineno, const char *what, const char *line)
{
	printf ("Line %d: %s\n    %.120s\n", lineno, what, line);
	errors++;
}

static const char *type_name (ax25_frame_type_t t)
{
	switch (t) {
	  case frame_type_I:		return "I";
	  case frame_type_S_RR:		return "RR";
	  case frame_type_S_RNR:	return "RNR";
	  case frame_type_S_REJ:	return "REJ";
	  case frame_type_S_SREJ:	return "SREJ";
	  case frame_type_U_SABME:	return "SABME";
	  case frame_type_U_SABM:	return "SABM";
	  case frame_type_U_DISC:	return "DISC";
	  case frame_type_U_DM:		return "DM";
	  case frame_type_U_UA:		return "UA";
	  case frame_type_U_FRMR:	return "FRMR";
	  case frame_type_U_UI:		return "UI";
	  case frame_type_U_XID:	return "XID";
	  case frame_type_U_TEST:	return "TEST";
	  case frame_type_U:		return "U";
	  default:			return "not AX.25";
	}
}

static const char *cr_name (cmdres_t cr)
{
	switch (cr) {
	  case cr_cmd:	return "cmd";
	  case cr_res:	return "res";
	  case cr_00:	return "00";
	  default:	return "11";
	}
}


int main (int argc, char *argv[])
{
	const char *fname = argc > 1 ? argv[1] : "ax25_vectors.txt";
	static char line[MAXLINE];
	static unsigned char frame[AX25_MAX_PACKET_LEN+100];
	static unsigned char packed[AX25_MAX_PACKET_LEN+2];
	int lineno = 0;
	int ntests = 0;

	text_color_init (0);

	FILE *fp = fopen (fname, "r");
	if (fp == NULL) {
	  printf ("Can't open %s\n", fname);
	  exit (EXIT_FAILURE);
	}

	while (fgets (line, sizeof(line), fp) != NULL) {
	  lineno++;
	  line[strcspn(line, "\r\n")] = '\0';
	  if (line[0] == '#' || line[0] == '\0') continue;
	  ntests++;

	  if (line[0] == 'T') {
	    // T <hex> <fcs> <monitor text>
	    char *h = line + 2;
	    char *f = strchr (h, ' ');
	    char *text = f ? strchr (f + 1, ' ') : NULL;
	    if (f == NULL || text == NULL) { fail (lineno, "bad line", line); continue; }
	    *f++ = '\0';
	    *text++ = '\0';
	    int flen = unhex (h, frame, sizeof(frame));
	    int fcs = strtol (f, NULL, 16);

	    if (fcs_calc (frame, flen) != fcs) fail (lineno, "FCS differs", text);

	    packet_t pp = ax25_from_text (text, 1);
	    if (pp == NULL) { fail (lineno, "ax25_from_text failed", text); continue; }
	    int plen = ax25_pack (pp, packed);
	    if (plen != flen || memcmp (packed, frame, flen) != 0) fail (lineno, "encoded bytes differ", text);
	    ax25_delete (pp);

	    // And back again.
	    pp = ax25_from_frame (frame, flen, (alevel_t){0});
	    if (pp == NULL) { fail (lineno, "ax25_from_frame failed", text); continue; }
	    char addrs[AX25_MAX_ADDRS*AX25_MAX_ADDR_LEN+8];
	    ax25_format_addrs (pp, addrs);
	    const char *colon = strchr (text, ':');
	    if (colon == NULL || strlen(addrs) != (size_t)(colon - text + 1) || strncmp (addrs, text, colon - text + 1) != 0) {
	      fail (lineno, "decoded addresses differ", addrs);
	    }
	    unsigned char *pinfo;
	    int ilen = ax25_get_info (pp, &pinfo);
	    int hdr = ax25_get_num_addr (pp) * 7 + 2;
	    if (ilen != flen - hdr || memcmp (pinfo, frame + hdr, ilen) != 0) fail (lineno, "decoded info differs", text);
	    if (ax25_get_control (pp) != 0x03 || ax25_get_pid (pp) != 0xf0) fail (lineno, "control/pid differ", text);
	    ax25_delete (pp);
	  }

	  else if (line[0] == 'R') {
	    // R <type> <cr> <pf> <nr> <ns> <modulo> <hex> <fcs>
	    char tname[16], crs[8], h[MAXLINE];
	    int pf, nr, ns, modulo;
	    unsigned int fcs;
	    if (sscanf (line + 2, "%15s %7s %d %d %d %d %9999s %x", tname, crs, &pf, &nr, &ns, &modulo, h, &fcs) != 8) {
	      fail (lineno, "bad line", line);
	      continue;
	    }
	    int flen = unhex (h, frame, sizeof(frame));
	    if ((unsigned int)fcs_calc (frame, flen) != fcs) fail (lineno, "FCS differs", line);

	    packet_t pp = ax25_from_frame (frame, flen, (alevel_t){0});
	    if (pp == NULL) { fail (lineno, "ax25_from_frame failed", line); continue; }
	    ax25_set_modulo (pp, modulo);

	    cmdres_t cr;
	    char desc[80];
	    int gpf, gnr, gns;
	    ax25_frame_type_t t = ax25_frame_type (pp, &cr, desc, &gpf, &gnr, &gns);
	    if (strcmp (type_name(t), tname) != 0 || strcmp (cr_name(cr), crs) != 0 ||
		gpf != pf || gnr != nr || gns != ns) {
	      char msg[200];
	      snprintf (msg, sizeof(msg), "expected %s %s pf=%d nr=%d ns=%d, got %s %s pf=%d nr=%d ns=%d (%s)",
			tname, crs, pf, nr, ns, type_name(t), cr_name(cr), gpf, gnr, gns, desc);
	      fail (lineno, msg, line);
	    }
	    int plen = ax25_pack (pp, packed);
	    if (plen != flen || memcmp (packed, frame, flen) != 0) fail (lineno, "frame not kept intact", line);
	    ax25_delete (pp);
	  }

	  else if (line[0] == 'L') {
	    // L <hex> NULL | <number of addresses>
	    char *h = line + 2;
	    char *e = strchr (h, ' ');
	    if (e == NULL) { fail (lineno, "bad line", line); continue; }
	    *e++ = '\0';
	    int flen = unhex (h, frame, sizeof(frame));
	    packet_t pp = ax25_from_frame (frame, flen, (alevel_t){0});
	    if (strcmp (e, "NULL") == 0) {
	      if (pp != NULL) { fail (lineno, "should have been rejected", h); ax25_delete (pp); }
	      continue;
	    }
	    if (pp == NULL) { fail (lineno, "should not have been rejected", h); continue; }
	    int want = atoi (e);
	    if (ax25_get_num_addr (pp) != want) {
	      char msg[80];
	      snprintf (msg, sizeof(msg), "expected %d addresses, got %d", want, ax25_get_num_addr (pp));
	      fail (lineno, msg, h);
	    }
	    if (want == 0) {
	      cmdres_t cr;
	      char desc[80];
	      int gpf, gnr, gns;
	      if (ax25_frame_type (pp, &cr, desc, &gpf, &gnr, &gns) != frame_not_AX25) fail (lineno, "should not be AX.25", h);
	    }
	    ax25_delete (pp);
	  }

	  else {
	    fail (lineno, "unknown line type", line);
	  }
	}
	fclose (fp);

	if (errors != 0 || ntests == 0) {
	  printf ("\nax25golden_test: %d errors in %d vectors.\n", errors, ntests);
	  exit (EXIT_FAILURE);
	}
	printf ("\nax25golden_test: all %d vectors OK.\n", ntests);
	exit (EXIT_SUCCESS);
}
