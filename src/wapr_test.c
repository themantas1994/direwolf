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
 * Module:      wapr_test.c
 *
 * Purpose:   	Tests for the experimental WAPR codec and modem.
 *
 *		- Golden vectors from the Python reference (test/wapr/golden):
 *		  frame packing, tone sequence, decoding, malformed headers.
 *		- CRC, LDPC decoding in noise.
 *		- Modem loopback: every profile, several sample rates, random
 *		  start and frequency offset; noise; pure noise; corrupted frames.
 *
 * Usage:	wapr_test [wapr_vectors.txt]
 *		wapr_test -m PROFILE SNR_DB FRAMES RATE [OFFSET_HZ]
 *			measure delivery in AWGN (SNR in 2500 Hz, as in
 *			test/wapr/README.md) and decode time.
 *		wapr_test -c PROFILE SIR_DB FRAMES RATE [SNR_DB [OVERLAP]]
 *			two overlapping frames, the second SIR_DB weaker.
 *		wapr_test -r PROFILE RATE FILE
 *			decode buffers written by test/wapr/wapr_crosscheck.py.
 *
 *		All random numbers come from a fixed seed: results repeat.
 *
 *---------------------------------------------------------------*/

#include "direwolf.h"

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <ctype.h>
#include <math.h>
#include <time.h>

#include "wapr.h"
#include "wapr_link.h"

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

static int errors = 0;

#define CHECK(cond, ...) do { if (! (cond)) { printf ("FAIL: " __VA_ARGS__); printf ("\n"); errors++; } } while (0)


/* xorshift64* and Box-Muller: same numbers on every platform. */

static unsigned long long rng_state = 0x9E3779B97F4A7C15ULL;

static unsigned long long rnd64 (void)
{
	rng_state ^= rng_state >> 12;
	rng_state ^= rng_state << 25;
	rng_state ^= rng_state >> 27;
	return (rng_state * 2685821657736338717ULL);
}

static double uniform (void)
{
	return ((rnd64() >> 11) * (1.0 / 9007199254740992.0));
}

static double gauss (void)
{
	double u = uniform(), v = uniform();
	if (u < 1e-300) u = 1e-300;
	return (sqrt(-2 * log(u)) * cos(2 * M_PI * v));
}


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


/* Ideal LLRs (transmitted order) for a tone sequence. */

static void ideal_llr (const wapr_profile_t *p, const unsigned char *syms, float scale, float *llr)
{
	wapr_layout_t lay;
	wapr_layout (p, &lay);
	int i = 0;
	for (int k = 0; k < lay.n_sym; k++) {
	  if (lay.is_sync[k]) continue;
	  int g = syms[k] ^ (syms[k] >> 1);
	  for (int b = lay.bits_per_sym - 1; b >= 0; b--) {
	    if (i < WAPR_CODE_BITS) llr[i] = ((g >> b) & 1) ? -scale : scale;
	    i++;
	  }
	}
}


static void test_golden (const char *fname)
{
	FILE *fp = fopen (fname, "r");
	char line[4000];
	int nv = 0, nx = 0;
	const wapr_profile_t *p = wapr_profile_find ("H150");

	if (fp == NULL) {
	  printf ("FAIL: can't open %s\n", fname);
	  errors++;
	  return;
	}
	while (fgets(line, sizeof(line), fp) != NULL) {
	  if (line[0] == 'V') {
	    int type, ack, seq;
	    char src[32], dst[32], plhex[200], infohex[200], symstr[600];
	    if (sscanf(line, "V %d %d %d %31s %31s %199s %199s %599s", &type, &ack, &seq, src, dst, plhex, infohex, symstr) != 8) {
	      printf ("FAIL: bad vector line %s", line);
	      errors++;
	      continue;
	    }
	    wapr_frame_t f, g;
	    memset (&f, 0, sizeof(f));
	    f.type = type; f.ack = ack; f.seq = seq;
	    strlcpy (f.source, src, sizeof(f.source));
	    strlcpy (f.dest, strcmp(dst, "-") == 0 ? "" : dst, sizeof(f.dest));
	    f.len = strcmp(plhex, "-") == 0 ? 0 : unhex(plhex, f.payload, WAPR_PAYLOAD_AREA);

	    unsigned char info[WAPR_INFO_BYTES], want[WAPR_INFO_BYTES];
	    CHECK (unhex(infohex, want, WAPR_INFO_BYTES) == WAPR_INFO_BYTES, "info length, vector %d", nv);
	    CHECK (wapr_frame_pack(&f, info) == WAPR_OK, "pack vector %d", nv);
	    CHECK (memcmp(info, want, WAPR_INFO_BYTES) == 0, "packed bits differ, vector %d", nv);
	    /* unpack gives the canonical form ("-0" dropped), so compare by packing again */
	    unsigned char again[WAPR_INFO_BYTES];
	    CHECK (wapr_frame_unpack(want, &g) == WAPR_OK && wapr_frame_pack(&g, again) == WAPR_OK &&
		   memcmp(again, want, WAPR_INFO_BYTES) == 0 && g.len == f.len && g.seq == f.seq, "unpack vector %d", nv);

	    unsigned char syms[WAPR_MAX_SYMBOLS];
	    int ns = wapr_encode (p, want, syms);
	    int same = ns == (int)strlen(symstr);
	    for (int k = 0; same && k < ns; k++) same = syms[k] == symstr[k] - '0';
	    CHECK (same, "tone sequence differs from reference, vector %d", nv);

	    float llr[WAPR_CODE_BITS];
	    unsigned char back[WAPR_INFO_BYTES];
	    ideal_llr (p, syms, 4.0f, llr);
	    CHECK (wapr_decode(llr, back, NULL) == WAPR_OK && memcmp(back, want, WAPR_INFO_BYTES) == 0, "decode vector %d", nv);
	    nv++;
	  }
	  else if (line[0] == 'X') {
	    char reason[40], infohex[200];
	    static const struct { const char *r; int e; } map[] = {
		{ "version", WAPR_ERR_VERSION }, { "type", WAPR_ERR_TYPE }, { "reserved_flag", WAPR_ERR_FLAGS },
		{ "length", WAPR_ERR_LENGTH }, { "padding", WAPR_ERR_PADDING },
		{ "broadcast_source", WAPR_ERR_ADDRESS }, { "address_range", WAPR_ERR_ADDRESS } };
	    unsigned char info[WAPR_INFO_BYTES];
	    wapr_frame_t g;
	    if (sscanf(line, "X %39s %199s", reason, infohex) != 2) continue;
	    unhex (infohex, info, WAPR_INFO_BYTES);
	    int want = 1, got = wapr_frame_unpack (info, &g);
	    for (size_t i = 0; i < sizeof(map) / sizeof(map[0]); i++) if (strcmp(map[i].r, reason) == 0) want = map[i].e;
	    CHECK (got == want, "malformed '%s': got %d (%s), want %d", reason, got, wapr_strerror(got), want);
	    nx++;
	  }
	}
	fclose (fp);
	CHECK (nv >= 8 && nx >= 7, "too few vectors (%d, %d)", nv, nx);
	printf ("golden vectors: %d frames, %d malformed headers\n", nv, nx);
}


static void test_pack_errors (void)
{
	wapr_frame_t f;
	unsigned char info[WAPR_INFO_BYTES];
	static const char *bad[] = { "", "TOOLONG", "N0-CALL", "N0CALL-16", "N0CALL-", "N0 C", "N0CALL-1x" };

	memset (&f, 0, sizeof(f));
	f.type = WAPR_TYPE_APRS;
	for (size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); i++) {
	  strlcpy (f.source, bad[i], sizeof(f.source));
	  CHECK (wapr_frame_pack(&f, info) == WAPR_ERR_ADDRESS, "source '%s' accepted", bad[i]);
	}
	strlcpy (f.source, "N0CALL", sizeof(f.source));
	f.len = WAPR_PAYLOAD_AREA + 1;
	CHECK (wapr_frame_pack(&f, info) == WAPR_ERR_LENGTH, "excessive length accepted");
	f.len = 0; f.seq = 1024;
	CHECK (wapr_frame_pack(&f, info) == WAPR_ERR_SEQ, "sequence 1024 accepted");
	f.seq = 0; f.type = 9;
	CHECK (wapr_frame_pack(&f, info) == WAPR_ERR_TYPE, "type 9 accepted");
}


/* Arbitrary information blocks: never crash; anything accepted is canonical. */

static void test_unpack_random (void)
{
	int accepted = 0, noncanon = 0;
	for (int t = 0; t < 200000; t++) {
	  unsigned char info[WAPR_INFO_BYTES], again[WAPR_INFO_BYTES];
	  wapr_frame_t f;
	  for (int i = 0; i < WAPR_INFO_BYTES; i++) info[i] = rnd64() & 0xff;
	  /* make about half of them pass the cheap header checks so the deeper ones run */
	  if (t & 1) {
	    info[0] = ((rnd64() & 1) << 2) | (info[0] & 0x02);	/* version 0, type 0 or 1, reserved flag 0 */
	    info[1] = (info[1] & 0x03) | ((rnd64() % 33) << 2);	/* length 0 .. 32 */
	    int len = info[1] >> 2;
	    memset (info + WAPR_HEADER_BYTES + len, 0, WAPR_PAYLOAD_AREA - len);
	  }
	  if (wapr_frame_unpack(info, &f) == WAPR_OK) {
	    accepted++;
	    if (wapr_frame_pack(&f, again) != WAPR_OK || memcmp(again, info, WAPR_INFO_BYTES) != 0) noncanon++;
	  }
	}
	CHECK (accepted > 1000 && noncanon == 0, "random headers: %d accepted, %d not canonical", accepted, noncanon);
	printf ("random information blocks: %d of 200000 accepted, all canonical\n", accepted - noncanon);

	/* Pure noise LLRs must never decode. */
	int ok = 0;
	for (int t = 0; t < 2000; t++) {
	  float llr[WAPR_CODE_BITS];
	  unsigned char info[WAPR_INFO_BYTES];
	  for (int i = 0; i < WAPR_CODE_BITS; i++) llr[i] = (float)(2 * gauss());
	  ok += wapr_decode (llr, info, NULL) == WAPR_OK;
	}
	CHECK (ok == 0, "%d of 2000 noise LLR blocks decoded", ok);
}


static void test_crc_ldpc (void)
{
	unsigned char bits[WAPR_INFO_BITS];
	for (int i = 0; i < WAPR_INFO_BITS; i++) bits[i] = rnd64() & 1;
	unsigned int c = wapr_crc24 (bits, WAPR_INFO_BITS);
	int miss = 0;
	for (int i = 0; i < WAPR_INFO_BITS; i++) {
	  bits[i] ^= 1;
	  miss += wapr_crc24(bits, WAPR_INFO_BITS) == c;
	  bits[i] ^= 1;
	}
	CHECK (miss == 0, "CRC missed %d single bit errors", miss);
	memset (bits, 0, sizeof(bits));
	CHECK (wapr_crc24(bits, WAPR_INFO_BITS) != 0, "CRC of zeros is zero");

	/* BPSK in AWGN at Eb/N0 = 3 dB: the Python reference had no frame errors in 300 at 2.5 dB. */
	unsigned char msg[WAPR_MSG_BITS], code[WAPR_CODE_BITS], dec[WAPR_MSG_BITS];
	float llr[WAPR_CODE_BITS];
	double sigma = sqrt(1.0 / (2 * 0.5 * pow(10, 0.3)));
	int fe = 0, N = 200;
	for (int t = 0; t < N; t++) {
	  for (int i = 0; i < WAPR_MSG_BITS; i++) msg[i] = rnd64() & 1;
	  wapr_ldpc_encode (msg, code);
	  for (int i = 0; i < WAPR_CODE_BITS; i++) {
	    double y = (code[i] ? -1.0 : 1.0) + sigma * gauss();
	    llr[i] = (float)(2 * y / (sigma * sigma));
	  }
	  fe += wapr_ldpc_decode(llr, dec, 50) < 0 || memcmp(dec, msg, WAPR_MSG_BITS) != 0;
	}
	CHECK (fe <= 2, "LDPC: %d frame errors in %d at Eb/N0 3 dB", fe, N);
	printf ("LDPC at Eb/N0 3 dB: %d / %d frame errors\n", fe, N);
}


/*
 * One frame in a buffer of noise: random lead in, frequency offset fo,
 * SNR_2500 snr_db (or no noise if snr_db > 100).
 * Returns 1 delivered, 0 nothing, -1 wrong frame accepted.
 */

static int one_frame (const wapr_profile_t *p, int fs, double snr_db, double fo, wapr_frame_t *f, int *ncand, double *cpu)
{
	unsigned char info[WAPR_INFO_BYTES], syms[WAPR_MAX_SYMBOLS];
	wapr_frame_pack (f, info);
	int ns = wapr_encode (p, info, syms);
	int nf = wapr_samples_needed (p, fs);
	int lead = (int)((0.25 + 0.75 * uniform()) * nf) + (int)(fs / p->baud);
	int n = lead + nf + nf / 4 + (int)(fs / p->baud);
	float *x = calloc (n, sizeof(float));
	float *s = malloc (sizeof(float) * nf);
	wapr_profile_t q = *p;
	q.f_center += fo;		/* transmitter (or SSB receiver) off frequency */
	int m = wapr_modulate (&q, syms, ns, fs, 1.0f, s, nf);
	double ps = 0;
	for (int i = 0; i < m; i++) ps += s[i] * (double)s[i];
	ps /= m;
	double sigma = snr_db > 100 ? 0 : sqrt(ps / (2500.0 * pow(10, snr_db / 10)) * fs / 2);
	for (int i = 0; i < n; i++) x[i] = (float)(sigma * gauss());
	for (int i = 0; i < m; i++) x[lead + i] += s[i];

	wapr_rx_result_t res[4];
	clock_t c0 = clock();
	int got = wapr_receive (p, x, n, fs, res, 4, ncand);
	if (cpu) *cpu += (double)(clock() - c0) / CLOCKS_PER_SEC;
	free (x);
	free (s);
	int r = 0;
	for (int i = 0; i < got; i++) {
	  if (memcmp(&res[i].frame, f, sizeof(*f)) == 0) r = r == 0 ? 1 : r;
	  else r = -1;
	}
	return (r);
}

static void random_frame (wapr_frame_t *f, int i)
{
	memset (f, 0, sizeof(*f));
	f->type = WAPR_TYPE_APRS;
	f->seq = i & 1023;
	unsigned u = (unsigned)i;
	if (u % 16) snprintf (f->source, sizeof(f->source), "N%uTST-%u", u % 10, u % 16);
	else snprintf (f->source, sizeof(f->source), "N%uTST", u % 10);	/* canonical: no "-0" */
	f->len = (int)(rnd64() % (WAPR_PAYLOAD_AREA + 1));
	for (int j = 0; j < f->len; j++) f->payload[j] = 32 + rnd64() % 95;
}


static void test_loopback (void)
{
	static const char *names[] = { "F600", "H150", "R25" };
	static const int rates[] = { 12000, 44100, 48000 };

	for (int i = 0; i < 3; i++) {
	  const wapr_profile_t *p = wapr_profile_find (names[i]);
	  for (int r = 0; r < 3; r++) {
	    int ok = 0, bad = 0;
	    int n = p->baud < 100 ? 1 : 3;
	    for (int k = 0; k < n; k++) {
	      wapr_frame_t f;
	      random_frame (&f, k);
	      double fo = (2 * uniform() - 1) * 0.9 * p->freq_search;
	      int res = one_frame (p, rates[r], 1000, fo, &f, NULL, NULL);
	      ok += res == 1;
	      bad += res < 0;
	    }
	    CHECK (ok == n && bad == 0, "%s at %d Hz: noise free loopback %d / %d, %d wrong", names[i], rates[r], ok, n, bad);
	  }
	}
	printf ("noise free loopback: F600, H150, R25 at 12000, 44100, 48000 Hz done\n");
}


static void test_noise (void)
{
	const wapr_profile_t *p = wapr_profile_find ("H150");
	int fs = 12000;

	/* Delivery a little above the simulated 90 % threshold. */
	int ok = 0, bad = 0, N = 20;
	for (int k = 0; k < N; k++) {
	  wapr_frame_t f;
	  random_frame (&f, k);
	  int r = one_frame (p, fs, -3.0, (2 * uniform() - 1) * 40, &f, NULL, NULL);
	  ok += r == 1;
	  bad += r < 0;
	}
	CHECK (ok >= N - 2 && bad == 0, "H150 at SNR -3 dB: %d / %d delivered, %d wrong", ok, N, bad);
	printf ("H150 at -3 dB, 12000 Hz: %d / %d delivered\n", ok, N);

	/* Pure noise: nothing may be decoded. */
	int n = fs * 60;
	float *x = malloc (sizeof(float) * n);
	for (int i = 0; i < n; i++) x[i] = (float)(1000 * gauss());
	wapr_rx_result_t res[4];
	int tried = 0, got = 0;
	int chunk = fs * 6;
	for (int i = 0; i + chunk <= n; i += chunk) {
	  int t;
	  got += wapr_receive (p, x + i, chunk, fs, res, 4, &t);
	  tried += t;
	}
	free (x);
	CHECK (got == 0, "%d frames decoded from pure noise", got);
	printf ("pure noise, 60 s: %d candidates decoded, %d frames\n", tried, got);

	/* Corrupted frames: tones replaced at random; a wrong frame must never come out. */
	int wrong = 0, right = 0;
	for (int k = 0; k < 20; k++) {
	  wapr_frame_t f;
	  unsigned char info[WAPR_INFO_BYTES], syms[WAPR_MAX_SYMBOLS];
	  random_frame (&f, k);
	  wapr_frame_pack (&f, info);
	  int ns = wapr_encode (p, info, syms);
	  for (int j = 0; j < ns; j++) if (uniform() < 0.35) syms[j] = rnd64() % p->tones;
	  int nf = wapr_samples_needed (p, fs);
	  float *s = calloc (nf + fs, sizeof(float));
	  wapr_modulate (p, syms, ns, fs, 1.0f, s + fs / 2, nf);
	  got = wapr_receive (p, s, nf + fs, fs, res, 4, NULL);
	  for (int i = 0; i < got; i++) {
	    if (memcmp(&res[i].frame, &f, sizeof(f)) == 0) right++; else wrong++;
	  }
	  free (s);
	}
	CHECK (wrong == 0, "%d wrong frames accepted from corrupted transmissions", wrong);
	printf ("35 %% of tones corrupted: %d / 20 still delivered, %d wrong\n", right, wrong);
}


/*
 * Link layer: station A sends addressed messages to B over a channel that loses each
 * frame (either direction) with probability p.  Event driven, deterministic.
 */

static void test_link (void)
{
	const wapr_profile_t *p = wapr_profile_find ("H150");
	wapr_link_t A, B;
	const double loss = 0.3;
	const int M = 400;
	int delivered_twice = 0, delivered = 0, max_tx = 0;
	long a_gave_up0;

	wapr_link_init (&A, p, 0, 11);
	wapr_link_init (&B, p, 0, 22);
	double t = 1000.0;
	for (int m = 0; m < M; m++) {
	  wapr_frame_t f;
	  memset (&f, 0, sizeof(f));
	  f.type = WAPR_TYPE_APRS;
	  strlcpy (f.source, "N0AAA", sizeof(f.source));
	  strlcpy (f.dest, "N0BBB", sizeof(f.dest));
	  f.ack = 1;
	  f.len = snprintf ((char *)f.payload, sizeof(f.payload), ":N0BBB    :msg %d", m);
	  int got = 0, ntx = 0;
	  a_gave_up0 = A.gave_up;
	  wapr_frame_t out = f;
	  int have = 1;				/* a frame waiting to be sent */
	  double end = t + 120;
	  while (t < end) {
	    if (have) {
	      have = 0;
	      if (wapr_link_tx(&A, &out, t) == 0) {
	        ntx++;
	        if (uniform() > loss) {		/* B hears it */
	          int send_ack;
	          wapr_frame_t ack;
	          int what = wapr_link_rx (&B, &out, "N0BBB", t + A.airtime, &send_ack, &ack);
	          if (what == WAPR_LINK_DELIVER) got++;
	          if (send_ack) {
	            wapr_link_tx (&B, &ack, t + 1.5 * A.airtime);
	            if (uniform() > loss) {	/* A hears the acknowledgement */
	              int sa;
	              wapr_frame_t dummy;
	              wapr_link_rx (&A, &ack, "N0AAA", t + 2.5 * A.airtime, &sa, &dummy);
	            }
	          }
	        }
	      }
	    }
	    t += 0.1;
	    wapr_frame_t again;
	    if (wapr_link_poll(&A, t, &again)) {
	      out = again;
	      have = 1;
	    }
	  }
	  delivered += got > 0;
	  delivered_twice += got > 1;
	  if (ntx > max_tx) max_tx = ntx;
	  (void)a_gave_up0;
	}
	/* expected: delivered unless all 3 copies lost; sender gives up unless one round trip works */
	double p_deliv = 1 - pow(loss, 3);
	double p_ack = 1 - pow(1 - (1 - loss) * (1 - loss), 3);
	double sd = sqrt(p_deliv * (1 - p_deliv) / M);
	CHECK (delivered_twice == 0, "link: %d messages delivered more than once", delivered_twice);
	CHECK (max_tx <= 3, "link: a message was sent %d times", max_tx);
	CHECK (fabs((double)delivered / M - p_deliv) < 4 * sd + 0.01, "link: delivered %d / %d, expected %.3f", delivered, M, p_deliv);
	CHECK (fabs((double)A.acked / M - p_ack) < 4 * sqrt(p_ack * (1 - p_ack) / M) + 0.01,
		"link: acknowledged %ld / %d, expected %.3f", A.acked, M, p_ack);
	printf ("link, 30 %% loss each way: delivered %d / %d (expected %.1f %%), acknowledged %ld, gave up %ld, retransmissions %ld, duplicates suppressed %ld\n",
		delivered, M, 100 * p_deliv, A.acked, A.gave_up, A.retries, B.duplicates);

	/* Broadcasts never wait for an acknowledgement. */
	wapr_link_init (&A, p, 0, 5);
	wapr_frame_t b;
	memset (&b, 0, sizeof(b));
	b.type = WAPR_TYPE_APRS;
	strlcpy (b.source, "N0AAA", sizeof(b.source));
	b.len = 3;
	memcpy (b.payload, ">hi", 3);
	wapr_link_tx (&A, &b, 0);
	wapr_frame_t again;
	CHECK (! wapr_link_poll(&A, 1e6, &again), "link: broadcast frame was retransmitted");

	/* Airtime limit 10 %: a burst uses the bucket (60 s), then one frame per airtime / 0.1. */
	wapr_link_init (&A, p, 0.10, 6);
	int sent = 0, refused = 0;
	for (int i = 0; i < 300; i++) {
	  wapr_frame_t f2 = b;
	  if (wapr_link_tx(&A, &f2, 2000.0 + i) == 0) sent++; else refused++;
	}
	double allowed = (0.10 * 600 + 0.10 * 300) / A.airtime;
	CHECK (sent <= (int)allowed + 1 && sent >= (int)allowed - 2, "link: airtime limit let %d frames through in 300 s, expected about %.1f", sent, allowed);
	wapr_frame_t ackf;
	memset (&ackf, 0, sizeof(ackf));
	ackf.type = WAPR_TYPE_LINK_ACK;
	strlcpy (ackf.source, "N0AAA", sizeof(ackf.source));
	strlcpy (ackf.dest, "N0BBB", sizeof(ackf.dest));
	CHECK (wapr_link_tx(&A, &ackf, 2300.0) == 0, "link: acknowledgement refused by the airtime limit");
	printf ("link, airtime limit 10 %%: %d frames sent and %d refused in 300 s (expected about %.0f sent)\n", sent, refused, allowed);
}


/* Channel busy detector: noise, a frame, noise. */

static void test_dcd (void)
{
	const wapr_profile_t *p = wapr_profile_find ("H150");
	int fs = 48000;
	unsigned char info[WAPR_INFO_BYTES], syms[WAPR_MAX_SYMBOLS];
	wapr_frame_t f;
	random_frame (&f, 3);
	wapr_frame_pack (&f, info);
	int ns = wapr_encode (p, info, syms);
	int nf = wapr_samples_needed (p, fs);
	float *s = malloc (sizeof(float) * nf);
	int m = wapr_modulate (p, syms, ns, fs, 1.0f, s, nf);
	double ps = 0;
	for (int i = 0; i < m; i++) ps += s[i] * (double)s[i];
	ps /= m;

	static const double snrs[] = { 10.0, -5.0 };
	for (int t = 0; t < 2; t++) {
	  double sigma = 1000.0;
	  /* scale the frame so that SNR_2500 is snrs[t] for this noise */
	  double n0 = 2 * sigma * sigma / fs;
	  double amp = sqrt(n0 * 2500.0 * pow(10, snrs[t] / 10) / ps);
	  int lead = 3 * fs, tail = 2 * fs;
	  int n = lead + m + tail;
	  wapr_dcd_t d;
	  wapr_dcd_init (&d, p, fs);
	  int false_busy = 0, on_at = -1, off_at = -1, flicker = 0;
	  for (int i = 0; i < n; i++) {
	    double x = sigma * gauss();
	    if (i >= lead && i < lead + m) x += amp * s[i - lead];
	    int c = wapr_dcd_sample (&d, (int)lrint(x));
	    if (c > 0 && i < lead) false_busy++;
	    if (c > 0 && i >= lead && on_at < 0) on_at = i - lead;
	    if (c < 0 && i >= lead + m && off_at < 0) off_at = i - lead - m;
	    if (c < 0 && i >= lead && i < lead + m) flicker++;
	    if (c > 0 && off_at >= 0) false_busy++;
	  }
	  if (t == 0) {
	    CHECK (false_busy == 0, "DCD: busy %d times on noise alone", false_busy);
	    CHECK (flicker == 0, "DCD: clear %d times during the frame", flicker);
	    CHECK (on_at >= 0 && on_at < fs / 10, "DCD: frame at +10 dB seen after %d samples", on_at);
	    CHECK (off_at >= 0 && off_at < fs / 4, "DCD: clear %d samples after the frame", off_at);
	    printf ("DCD, H150 at SNR_2500 +10 dB: busy after %.0f ms, clear %.0f ms after the end, %d false\n",
		1000.0 * on_at / fs, 1000.0 * off_at / fs, false_busy);
	  }
	  else {
	    printf ("DCD, H150 at SNR_2500 -5 dB (decodable, below the noise in band): %s\n",
		on_at >= 0 ? "seen" : "not seen (expected limitation)");
	  }
	}
	free (s);
}


static int measure (int argc, char *argv[])
{
	const wapr_profile_t *p = wapr_profile_find (argv[2]);
	if (p == NULL || argc < 6) {
	  printf ("usage: wapr_test -m PROFILE SNR_DB FRAMES RATE [OFFSET_HZ]\n");
	  return (EXIT_FAILURE);
	}
	double snr = atof(argv[3]);
	int N = atoi(argv[4]);
	int fs = atoi(argv[5]);
	double off = argc > 6 ? atof(argv[6]) : 0;
	int ok = 0, bad = 0, cand = 0;
	double cpu = 0;
	for (int k = 0; k < N; k++) {
	  wapr_frame_t f;
	  int c = 0;
	  random_frame (&f, k);
	  int r = one_frame (p, fs, snr, (2 * uniform() - 1) * off, &f, &c, &cpu);
	  ok += r == 1;
	  bad += r < 0;
	  cand += c;
	}
	printf ("%s fs=%d SNR_2500=%.2f dB: delivered %d / %d, wrong %d, candidates %.2f / frame, %.1f ms CPU / frame\n",
		p->name, fs, snr, ok, N, bad, (double)cand / N, 1000 * cpu / N);
	return (EXIT_SUCCESS);
}


/*
 * Collisions: a wanted frame at SNR_2500 snr and an interfering frame sir_db weaker,
 * starting at a random time from one frame before to one frame after the wanted one
 * (so every amount of overlap occurs), both at random offsets within +-25 Hz.
 * Prints how often the wanted, the interfering, and both frames were delivered.
 */

static int collide (int argc, char *argv[])
{
	const wapr_profile_t *p = argc > 5 ? wapr_profile_find (argv[2]) : NULL;
	if (p == NULL) {
	  printf ("usage: wapr_test -c PROFILE SIR_DB FRAMES RATE [SNR_DB [OVERLAP]]\n");
	  return (EXIT_FAILURE);
	}
	double sir = atof(argv[3]);
	int N = atoi(argv[4]);
	int fs = atoi(argv[5]);
	double snr = argc > 6 ? atof(argv[6]) : 10.0;
	double overlap = argc > 7 ? atof(argv[7]) : -1;	/* fraction of the frame, -1 = random */
	int nf = wapr_samples_needed (p, fs);
	int gap = (int)(fs / p->baud) * 4;
	int n = 3 * nf + 2 * gap;
	float *x = malloc (sizeof(float) * n);
	float *s = malloc (sizeof(float) * nf);
	int got_a = 0, got_b = 0, both = 0, wrong = 0;

	for (int k = 0; k < N; k++) {
	  wapr_frame_t fa, fb;
	  random_frame (&fa, 2 * k);
	  random_frame (&fb, 2 * k + 1);
	  fb.seq = (fa.seq + 1) & 1023;			/* always different frames */
	  int a0 = nf + gap;
	  int b0 = gap + (int)(uniform() * 2 * nf);	/* -nf .. +nf relative to a0 */
	  if (overlap >= 0) {				/* before or after, at random */
	    int shift = (int)((1 - overlap) * nf);
	    b0 = a0 + (uniform() < 0.5 ? -shift : shift);
	  }
	  double ps = 0;
	  memset (x, 0, sizeof(float) * n);
	  for (int w = 0; w < 2; w++) {
	    unsigned char info[WAPR_INFO_BYTES], syms[WAPR_MAX_SYMBOLS];
	    wapr_frame_pack (w == 0 ? &fa : &fb, info);
	    int ns = wapr_encode (p, info, syms);
	    wapr_profile_t q = *p;
	    q.f_center += (2 * uniform() - 1) * 25.0;
	    int m = wapr_modulate (&q, syms, ns, fs, 1.0f, s, nf);
	    double g = w == 0 ? 1.0 : pow(10, -sir / 20);
	    if (w == 0) {
	      for (int i = 0; i < m; i++) ps += s[i] * (double)s[i];
	      ps /= m;
	    }
	    int off = w == 0 ? a0 : b0;
	    for (int i = 0; i < m && off + i < n; i++) x[off + i] += (float)(g * s[i]);
	  }
	  double sigma = sqrt(ps / (2500.0 * pow(10, snr / 10)) * fs / 2);
	  for (int i = 0; i < n; i++) x[i] += (float)(sigma * gauss());

	  wapr_rx_result_t res[8];
	  int nr = wapr_receive (p, x, n, fs, res, 8, NULL);
	  int ra = 0, rb = 0;
	  for (int i = 0; i < nr; i++) {
	    if (memcmp(&res[i].frame, &fa, sizeof(fa)) == 0) ra = 1;
	    else if (memcmp(&res[i].frame, &fb, sizeof(fb)) == 0) rb = 1;
	    else wrong++;
	  }
	  got_a += ra; got_b += rb; both += ra && rb;
	}
	printf ("%s fs=%d SIR=%.1f dB SNR_2500=%.1f dB overlap=%.2f: wanted %d / %d, interferer %d, both %d, wrong %d\n",
		p->name, fs, sir, snr, overlap, got_a, N, got_b, both, wrong);
	free (x);
	free (s);
	return (EXIT_SUCCESS);
}


/*
 * Decode buffers made elsewhere (test/wapr/wapr_crosscheck.py), so the
 * C and Python receivers can be compared on identical audio.
 * Record: int32 sample count, float32 samples, 44 bytes expected information block.
 */

static int replay (int argc, char *argv[])
{
	const wapr_profile_t *p = argc > 4 ? wapr_profile_find (argv[2]) : NULL;
	FILE *fp = argc > 4 ? fopen (argv[4], "rb") : NULL;
	if (p == NULL || fp == NULL) {
	  printf ("usage: wapr_test -r PROFILE RATE FILE\n");
	  return (EXIT_FAILURE);
	}
	int fs = atoi(argv[3]);
	int n, rec = 0;
	while (fread(&n, sizeof(n), 1, fp) == 1) {
	  float *x = malloc (sizeof(float) * n);
	  unsigned char want[WAPR_INFO_BYTES], got[WAPR_INFO_BYTES];
	  if (fread(x, sizeof(float), n, fp) != (size_t)n || fread(want, 1, WAPR_INFO_BYTES, fp) != WAPR_INFO_BYTES) {
	    free (x);
	    break;
	  }
	  wapr_rx_result_t res[4];
	  int k = wapr_receive (p, x, n, fs, res, 4, NULL);
	  int ok = 0, bad = 0;
	  for (int i = 0; i < k; i++) {
	    if (wapr_frame_pack(&res[i].frame, got) == WAPR_OK && memcmp(got, want, WAPR_INFO_BYTES) == 0) ok = 1; else bad++;
	  }
	  printf ("%d %d %d\n", rec, ok, bad);
	  free (x);
	  rec++;
	}
	fclose (fp);
	return (EXIT_SUCCESS);
}


int main (int argc, char *argv[])
{
	if (argc > 1 && strcmp(argv[1], "-m") == 0) {
	  return (measure(argc, argv));
	}
	if (argc > 1 && strcmp(argv[1], "-r") == 0) {
	  return (replay(argc, argv));
	}
	if (argc > 1 && strcmp(argv[1], "-c") == 0) {
	  return (collide(argc, argv));
	}
	test_golden (argc > 1 ? argv[1] : "wapr_vectors.txt");
	test_pack_errors ();
	test_unpack_random ();
	test_crc_ldpc ();
	test_loopback ();
	test_noise ();
	test_dcd ();
	test_link ();

	if (errors != 0) {
	  printf ("\nwapr_test: %d errors.\n", errors);
	  exit (EXIT_FAILURE);
	}
	printf ("\nwapr_test: all tests passed.\n");
	exit (EXIT_SUCCESS);
}
