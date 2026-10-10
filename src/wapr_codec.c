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
 * Module:      wapr_codec.c
 *
 * Purpose:   	WAPR (experimental) frame format and coding, from the
 *		information block to the tone sequence and back.
 *
 * Description:	The reference implementation is in test/wapr/ (Python); this must
 *		stay bit exact with it (checked by wapr_test against
 *		test/wapr/golden/wapr_vectors.txt).
 *
 *		info (12 byte header + 32 byte payload area)
 *		 -> append CRC-24 (poly 0x864CFB, init and final xor 0xFFFFFF)
 *		 -> xor with x^9 + x^5 + 1 sequence (seed 0x1FF)
 *		 -> IRA LDPC, rate 1/2 (wapr_tables.h)
 *		 -> interleave (wapr_tables.h)
 *		 -> Gray mapped M-FSK symbols, Costas sync blocks inserted.
 *
 *		The header format is described in test/wapr/wapr_frame.py
 *		and doc/wapr/.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <ctype.h>
#include <math.h>

#include "wapr.h"
#include "wapr_tables.h"

#if WAPR_K != WAPR_MSG_BITS || WAPR_N != WAPR_CODE_BITS
#error "wapr_tables.h does not match wapr.h"
#endif

#define WAPR_PARITY (WAPR_CODE_BITS - WAPR_MSG_BITS)
#define WAPR_EDGES (WAPR_MSG_BITS * WAPR_DV + 2 * WAPR_PARITY - 1)


/*------------------------------------------------------------------
 * Profiles: the stage 1 candidates (test/wapr/configs/stage1.json).
 *---------------------------------------------------------------*/

static const wapr_profile_t profiles[] = {
	/* name    M   baud    centre  BT   sync  search  amp window */
	{ "F600",  4, 600.0,  1800.0, 1.0,  6,    50.0,   8 },
	{ "H150",  4, 150.0,  1500.0, 1.0,  6,    50.0,   8 },
	{ "R25",   8,  25.0,  1500.0, 2.0,  6,    50.0,   8 },
};

const wapr_profile_t *wapr_profile_find (const char *name)
{
	for (size_t i = 0; i < sizeof(profiles) / sizeof(profiles[0]); i++) {
	  if (strcasecmp(name, profiles[i].name) == 0) {
	    return (&profiles[i]);
	  }
	}
	return (NULL);
}


const char *wapr_strerror (int err)
{
	switch (err) {
	  case WAPR_OK:			return ("ok");
	  case WAPR_ERR_VERSION:	return ("unsupported version");
	  case WAPR_ERR_TYPE:		return ("unsupported payload type");
	  case WAPR_ERR_FLAGS:		return ("reserved flag set");
	  case WAPR_ERR_LENGTH:		return ("bad payload length");
	  case WAPR_ERR_SEQ:		return ("bad sequence number");
	  case WAPR_ERR_ADDRESS:	return ("bad address");
	  case WAPR_ERR_PADDING:	return ("non-zero padding");
	  case WAPR_ERR_FEC:		return ("FEC decoding failed");
	  case WAPR_ERR_CRC:		return ("CRC mismatch");
	}
	return ("unknown error");
}


/*------------------------------------------------------------------
 * Frame header.
 *
 *	bits  field
 *	2     version (0)
 *	4     type
 *	2     flags: bit 1 ack requested, bit 0 reserved (0)
 *	6     payload length
 *	10    sequence number
 *	36    source address
 *	36    destination address, 0 = broadcast
 *
 * Address: callsign, 1 - 6 of A-Z 0-9, left justified, space padded,
 * as a base 37 number (space 0, digits 1-10, letters 11-36), * 16 + SSID.
 *---------------------------------------------------------------*/

#define ADDR_LIMIT (2565726409ULL * 16ULL)	/* 37^6 * 16 */

static int addr_pack (const char *call, unsigned long long *v)
{
	char base[8];
	int n = 0;
	int ssid = 0;
	const char *p = call;

	*v = 0;
	if (*call == '\0') {
	  return (0);		/* broadcast */
	}
	while (*p != '\0' && *p != '-') {
	  int c = toupper((unsigned char)*p);
	  if (n >= 6 || ! (isdigit(c) || (c >= 'A' && c <= 'Z'))) {
	    return (-1);
	  }
	  base[n++] = c;
	  p++;
	}
	if (n == 0) {
	  return (-1);
	}
	if (*p == '-') {
	  p++;
	  if (! isdigit((unsigned char)p[0]) || (p[1] != '\0' && ! (isdigit((unsigned char)p[1]) && p[2] == '\0'))) {
	    return (-1);
	  }
	  ssid = atoi(p);
	  if (ssid > 15) {
	    return (-1);
	  }
	}
	while (n < 6) {
	  base[n++] = ' ';
	}
	unsigned long long x = 0;
	for (int i = 0; i < 6; i++) {
	  int c = base[i];
	  int d = c == ' ' ? 0 : isdigit(c) ? c - '0' + 1 : c - 'A' + 11;
	  x = x * 37 + d;
	}
	*v = x * 16 + ssid;
	return (0);
}

static int addr_unpack (unsigned long long v, char *out)
{
	char base[7];

	out[0] = '\0';
	if (v == 0) {
	  return (0);
	}
	if (v >= ADDR_LIMIT) {
	  return (-1);
	}
	int ssid = v % 16;
	v /= 16;
	for (int i = 5; i >= 0; i--) {
	  int d = v % 37;
	  v /= 37;
	  base[i] = d == 0 ? ' ' : d <= 10 ? '0' + d - 1 : 'A' + d - 11;
	}
	base[6] = '\0';
	int n = 6;
	while (n > 0 && base[n-1] == ' ') {
	  n--;
	}
	if (n == 0 || memchr(base, ' ', n) != NULL) {
	  return (-1);		/* empty, leading or embedded space */
	}
	base[n] = '\0';
	if (ssid != 0) {
	  snprintf (out, WAPR_ADDR_LEN, "%s-%d", base, ssid);
	}
	else {
	  strlcpy (out, base, WAPR_ADDR_LEN);
	}
	return (0);
}

static void put_bits (unsigned char *buf, int *pos, unsigned long long val, int n)
{
	for (int i = n - 1; i >= 0; i--) {
	  int b = (val >> i) & 1;
	  if (b) buf[*pos / 8] |= 0x80 >> (*pos % 8);
	  (*pos)++;
	}
}

static unsigned long long get_bits (const unsigned char *buf, int *pos, int n)
{
	unsigned long long v = 0;
	for (int i = 0; i < n; i++) {
	  v = (v << 1) | ((buf[*pos / 8] >> (7 - *pos % 8)) & 1);
	  (*pos)++;
	}
	return (v);
}


int wapr_frame_pack (const wapr_frame_t *f, unsigned char info[WAPR_INFO_BYTES])
{
	unsigned long long src, dst;

	if (f->type != WAPR_TYPE_RAW && f->type != WAPR_TYPE_APRS) return (WAPR_ERR_TYPE);
	if (f->len < 0 || f->len > WAPR_PAYLOAD_AREA) return (WAPR_ERR_LENGTH);
	if (f->seq < 0 || f->seq > 1023) return (WAPR_ERR_SEQ);
	if (addr_pack(f->source, &src) != 0 || src == 0) return (WAPR_ERR_ADDRESS);
	if (addr_pack(f->dest, &dst) != 0) return (WAPR_ERR_ADDRESS);

	memset (info, 0, WAPR_INFO_BYTES);
	int pos = 0;
	put_bits (info, &pos, WAPR_VERSION, 2);
	put_bits (info, &pos, f->type, 4);
	put_bits (info, &pos, f->ack ? 2 : 0, 2);
	put_bits (info, &pos, f->len, 6);
	put_bits (info, &pos, f->seq, 10);
	put_bits (info, &pos, src, 36);
	put_bits (info, &pos, dst, 36);
	memcpy (info + WAPR_HEADER_BYTES, f->payload, f->len);
	return (WAPR_OK);
}


int wapr_frame_unpack (const unsigned char info[WAPR_INFO_BYTES], wapr_frame_t *f)
{
	int pos = 0;

	memset (f, 0, sizeof(*f));
	if (get_bits(info, &pos, 2) != WAPR_VERSION) return (WAPR_ERR_VERSION);
	f->type = get_bits(info, &pos, 4);
	if (f->type != WAPR_TYPE_RAW && f->type != WAPR_TYPE_APRS) return (WAPR_ERR_TYPE);
	int flags = get_bits(info, &pos, 2);
	if (flags & 1) return (WAPR_ERR_FLAGS);
	f->ack = (flags & 2) != 0;
	f->len = get_bits(info, &pos, 6);
	if (f->len > WAPR_PAYLOAD_AREA) return (WAPR_ERR_LENGTH);
	f->seq = get_bits(info, &pos, 10);
	unsigned long long src = get_bits(info, &pos, 36);
	unsigned long long dst = get_bits(info, &pos, 36);
	if (src == 0 || addr_unpack(src, f->source) != 0) return (WAPR_ERR_ADDRESS);
	if (addr_unpack(dst, f->dest) != 0) return (WAPR_ERR_ADDRESS);
	for (int i = f->len; i < WAPR_PAYLOAD_AREA; i++) {
	  if (info[WAPR_HEADER_BYTES + i] != 0) return (WAPR_ERR_PADDING);
	}
	memcpy (f->payload, info + WAPR_HEADER_BYTES, f->len);
	return (WAPR_OK);
}


/*------------------------------------------------------------------
 * CRC-24 and scrambler, on arrays of 0/1 bits.
 *---------------------------------------------------------------*/

unsigned int wapr_crc24 (const unsigned char *bits, int nbits)
{
	unsigned int reg = 0xFFFFFF;
	for (int i = 0; i < nbits; i++) {
	  unsigned int top = ((reg >> 23) & 1) ^ bits[i];
	  reg = (reg << 1) & 0xFFFFFF;
	  if (top) reg ^= 0x864CFB;
	}
	return (reg ^ 0xFFFFFF);
}

static void scramble (unsigned char *bits, int n)
{
	unsigned int reg = 0x1FF;
	for (int i = 0; i < n; i++) {
	  unsigned int b = ((reg >> 8) ^ (reg >> 4)) & 1;
	  bits[i] ^= b;
	  reg = ((reg << 1) | b) & 0x1FF;
	}
}


/*------------------------------------------------------------------
 * LDPC code.  Information bit v is in checks wapr_info_chk[v*DV ..],
 * parity bit j in checks j and j+1 (the last one only in check
 * PARITY-1), so parity j = parity j-1 xor (information part of check j).
 *---------------------------------------------------------------*/

void wapr_ldpc_encode (const unsigned char msg[WAPR_MSG_BITS], unsigned char code[WAPR_CODE_BITS])
{
	unsigned char s[WAPR_PARITY];

	memset (s, 0, sizeof(s));
	for (int v = 0; v < WAPR_MSG_BITS; v++) {
	  if (msg[v]) {
	    for (int e = 0; e < WAPR_DV; e++) {
	      s[wapr_info_chk[v * WAPR_DV + e]] ^= 1;
	    }
	  }
	}
	memcpy (code, msg, WAPR_MSG_BITS);
	unsigned char p = 0;
	for (int j = 0; j < WAPR_PARITY; j++) {
	  p ^= s[j];
	  code[WAPR_MSG_BITS + j] = p;
	}
}


/* Edges grouped by check node, built once per decode (cheap, no shared state). */

struct graph_s {
	short ev[WAPR_EDGES];		/* variable of each edge */
	short cstart[WAPR_PARITY + 1];	/* first edge of each check */
};

static void build_graph (struct graph_s *g)
{
	short cnt[WAPR_PARITY];
	short fill[WAPR_PARITY];

	memset (cnt, 0, sizeof(cnt));
	for (int i = 0; i < WAPR_MSG_BITS * WAPR_DV; i++) cnt[wapr_info_chk[i]]++;
	for (int j = 0; j < WAPR_PARITY; j++) {
	  cnt[j]++;
	  if (j + 1 < WAPR_PARITY) cnt[j+1]++;
	}
	g->cstart[0] = 0;
	for (int c = 0; c < WAPR_PARITY; c++) {
	  g->cstart[c+1] = g->cstart[c] + cnt[c];
	  fill[c] = g->cstart[c];
	}
	for (int v = 0; v < WAPR_MSG_BITS; v++) {
	  for (int e = 0; e < WAPR_DV; e++) {
	    int c = wapr_info_chk[v * WAPR_DV + e];
	    g->ev[fill[c]++] = v;
	  }
	}
	for (int j = 0; j < WAPR_PARITY; j++) {
	  g->ev[fill[j]++] = WAPR_MSG_BITS + j;
	  if (j + 1 < WAPR_PARITY) g->ev[fill[j+1]++] = WAPR_MSG_BITS + j;
	}
}

static inline double phi (double x)
{
	if (x < 1e-9) x = 1e-9;
	if (x > 30.0) x = 30.0;
	return (-log(tanh(x / 2.0)));
}

static int syndrome_ok (const struct graph_s *g, const unsigned char *hard)
{
	for (int c = 0; c < WAPR_PARITY; c++) {
	  int s = 0;
	  for (int e = g->cstart[c]; e < g->cstart[c+1]; e++) s ^= hard[g->ev[e]];
	  if (s) return (0);
	}
	return (1);
}


/*------------------------------------------------------------------
 *
 * Name:	wapr_ldpc_decode
 *
 * Purpose:	Sum-product (belief propagation) decoding, flooding schedule.
 *
 * Inputs:	llr	- channel LLR of each code bit, codeword order.
 *		max_iter
 *
 * Outputs:	msg	- hard decisions of the K message bits.
 *
 * Returns:	Iterations used (0 if the channel decisions were already a
 *		codeword) or -1 if no codeword was found.
 *
 *---------------------------------------------------------------*/

int wapr_ldpc_decode (const float llr[WAPR_CODE_BITS], unsigned char msg[WAPR_MSG_BITS], int max_iter)
{
	struct graph_s g;
	static const int N = WAPR_CODE_BITS;
	double *q = malloc (sizeof(double) * WAPR_EDGES);	/* variable to check */
	double *r = malloc (sizeof(double) * WAPR_EDGES);	/* check to variable */
	double *tot = malloc (sizeof(double) * N);
	unsigned char hard[WAPR_CODE_BITS];
	int result = -1;

	build_graph (&g);

	for (int v = 0; v < N; v++) {
	  double l = llr[v];
	  tot[v] = l > 30 ? 30 : l < -30 ? -30 : l;
	  hard[v] = tot[v] < 0;
	}
	if (syndrome_ok(&g, hard)) {
	  result = 0;
	}
	else {
	  for (int e = 0; e < WAPR_EDGES; e++) q[e] = tot[g.ev[e]];
	  for (int it = 1; it <= max_iter && result < 0; it++) {
	    for (int c = 0; c < WAPR_PARITY; c++) {
	      double sum = 0;
	      int neg = 0;
	      for (int e = g.cstart[c]; e < g.cstart[c+1]; e++) {
	        sum += phi(fabs(q[e]));
	        neg ^= q[e] < 0;
	      }
	      for (int e = g.cstart[c]; e < g.cstart[c+1]; e++) {
	        double m = phi(sum - phi(fabs(q[e])));
	        r[e] = (neg ^ (q[e] < 0)) ? -m : m;
	      }
	    }
	    for (int v = 0; v < N; v++) {
	      double l = llr[v];
	      tot[v] = l > 30 ? 30 : l < -30 ? -30 : l;
	    }
	    for (int e = 0; e < WAPR_EDGES; e++) tot[g.ev[e]] += r[e];
	    for (int v = 0; v < N; v++) hard[v] = tot[v] < 0;
	    if (syndrome_ok(&g, hard)) {
	      result = it;
	    }
	    for (int e = 0; e < WAPR_EDGES; e++) q[e] = tot[g.ev[e]] - r[e];
	  }
	}
	memcpy (msg, hard, WAPR_MSG_BITS);
	free (q);
	free (r);
	free (tot);
	return (result);
}


/*------------------------------------------------------------------
 * Symbol layout and mapping.
 *---------------------------------------------------------------*/

/* Lexicographically first Costas arrays (as found by test/wapr/wapr_phy.py). */
static const int costas4[4] = { 0, 1, 3, 2 };
static const int costas8[8] = { 0, 1, 4, 6, 5, 3, 7, 2 };

void wapr_layout (const wapr_profile_t *p, wapr_layout_t *lay)
{
	int M = p->tones;
	int bps = M == 8 ? 3 : 2;

	memset (lay, 0, sizeof(*lay));
	lay->bits_per_sym = bps;
	lay->n_data = (WAPR_CODE_BITS + bps - 1) / bps;
	for (int i = 0; i < M; i++) lay->costas[i] = M == 8 ? costas8[i] : costas4[i];

	int nb = p->sync_blocks;
	int q = nb > 1 ? lay->n_data / (nb - 1) : lay->n_data;
	int rem = nb > 1 ? lay->n_data % (nb - 1) : 0;
	int k = 0;
	for (int b = 0; b < nb; b++) {
	  for (int j = 0; j < M; j++) {
	    lay->is_sync[k] = 1;
	    lay->sync_tone[k] = lay->costas[j];
	    k++;
	  }
	  if (b < (nb > 1 ? nb - 1 : 1)) {
	    int part = q + (b < rem ? 1 : 0);
	    for (int j = 0; j < part; j++) lay->is_sync[k++] = 0;
	  }
	}
	lay->n_sym = k;
}

static int gray (int m) { return (m ^ (m >> 1)); }


int wapr_encode (const wapr_profile_t *p, const unsigned char info[WAPR_INFO_BYTES], unsigned char *syms)
{
	unsigned char msg[WAPR_MSG_BITS];
	unsigned char code[WAPR_CODE_BITS];
	unsigned char tx[WAPR_CODE_BITS + 8];
	wapr_layout_t lay;
	int inv[WAPR_MAX_TONES];

	for (int i = 0; i < WAPR_INFO_BITS; i++) msg[i] = (info[i/8] >> (7 - i%8)) & 1;
	unsigned int crc = wapr_crc24 (msg, WAPR_INFO_BITS);
	for (int i = 0; i < WAPR_CRC_BITS; i++) msg[WAPR_INFO_BITS + i] = (crc >> (23 - i)) & 1;
	scramble (msg, WAPR_MSG_BITS);
	wapr_ldpc_encode (msg, code);

	wapr_layout (p, &lay);
	memset (tx, 0, sizeof(tx));
	for (int i = 0; i < WAPR_CODE_BITS; i++) tx[i] = code[wapr_perm[i]];

	for (int m = 0; m < p->tones; m++) inv[gray(m)] = m;
	int d = 0;
	for (int k = 0; k < lay.n_sym; k++) {
	  if (lay.is_sync[k]) {
	    syms[k] = lay.sync_tone[k];
	  }
	  else {
	    int v = 0;
	    for (int b = 0; b < lay.bits_per_sym; b++) v = (v << 1) | tx[d * lay.bits_per_sym + b];
	    syms[k] = inv[v];
	    d++;
	  }
	}
	return (lay.n_sym);
}


int wapr_decode (const float llr_tx[WAPR_CODE_BITS], unsigned char info[WAPR_INFO_BYTES], int *iterations)
{
	float llr[WAPR_CODE_BITS];
	unsigned char msg[WAPR_MSG_BITS];

	for (int i = 0; i < WAPR_CODE_BITS; i++) llr[wapr_perm[i]] = llr_tx[i];
	int it = wapr_ldpc_decode (llr, msg, 50);
	if (iterations != NULL) *iterations = it;
	if (it < 0) return (WAPR_ERR_FEC);
	scramble (msg, WAPR_MSG_BITS);
	unsigned int crc = 0;
	for (int i = 0; i < WAPR_CRC_BITS; i++) crc = (crc << 1) | msg[WAPR_INFO_BITS + i];
	if (crc != wapr_crc24(msg, WAPR_INFO_BITS)) return (WAPR_ERR_CRC);
	memset (info, 0, WAPR_INFO_BYTES);
	for (int i = 0; i < WAPR_INFO_BITS; i++) info[i/8] |= msg[i] << (7 - i%8);
	return (WAPR_OK);
}
