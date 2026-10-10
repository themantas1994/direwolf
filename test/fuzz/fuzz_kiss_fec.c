/*
 * libFuzzer harness for the receive side of KISS and of the FEC layers:
 *
 *   selector 0	KISS byte stream from a client (TCP, serial, pty):
 *		kiss_rec_byte, its state machine and kiss_process_msg,
 *		including the commands that set TXDELAY etc.
 *   selector 1	FX.25: a valid correlation tag followed by arbitrary
 *		bytes as the Reed-Solomon block, through fx25_rec_bit: RS
 *		decoding, bit unstuffing, FCS check, frame extraction.
 *   selector 2	IL2P: preamble and sync word followed by arbitrary bytes,
 *		through il2p_rec_bit: header decoding, RS, descrambling,
 *		payload, conversion to AX.25.
 *
 * Valid tags and sync words are supplied by the harness so the fuzzer
 * reaches the decoders instead of spending its time looking for them.
 * Whatever comes out goes through ax25_from_frame and is discarded.
 *
 * Build from the src directory of a configured tree, see README.md.
 */

#include "direwolf.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include "ax25_pad.h"
#include "kiss_frame.h"
#include "fx25.h"
#include "il2p.h"
#include "dlq.h"
#include "textcolor.h"


/* Stand ins for the rest of Dire Wolf. */

static struct audio_s audio_config;

/* Frames that came out of each path.  Printed at exit if FUZZ_STATS is set, */
/* to check that the seeds really reach the decoders. */

static int frames_out[3];

__attribute__((destructor)) static void print_stats (void)
{
	if (getenv("FUZZ_STATS") != NULL) {
	  fprintf (stderr, "frames out: KISS %d, FX.25 %d, IL2P %d\n", frames_out[0], frames_out[1], frames_out[2]);
	}
}

void multi_modem_process_rec_frame (int chan, int subchan, int slice, unsigned char *fbuf, int flen,
				    alevel_t alevel, retry_t retries, fec_type_t fec_type)
{
	(void)chan; (void)subchan; (void)slice; (void)alevel; (void)retries;
	frames_out[fec_type == fec_type_il2p ? 2 : 1]++;
	packet_t pp = ax25_from_frame (fbuf, flen, alevel);
	if (pp != NULL) {
	  unsigned char out[AX25_MAX_PACKET_LEN];
	  char addrs[AX25_MAX_ADDRS*AX25_MAX_ADDR_LEN];
	  (void) ax25_pack (pp, out);
	  ax25_format_addrs (pp, addrs);
	  ax25_delete (pp);
	}
}

void tq_append (int chan, int prio, packet_t pp) { (void)chan; (void)prio; frames_out[0]++; if (pp) ax25_delete (pp); }
void xmit_set_txdelay (int channel, int value) { (void)channel; (void)value; }
void xmit_set_persist (int channel, int value) { (void)channel; (void)value; }
void xmit_set_slottime (int channel, int value) { (void)channel; (void)value; }
void xmit_set_txtail (int channel, int value) { (void)channel; (void)value; }
void xmit_set_fulldup (int channel, int value) { (void)channel; (void)value; }
void kissnet_copy (unsigned char *kiss_msg, int kiss_len, int chan, int cmd, struct kissport_status_s *from_kps, int from_client)
{
	(void)kiss_msg; (void)kiss_len; (void)chan; (void)cmd; (void)from_kps; (void)from_client;
}

void multi_modem_process_rec_packet (int chan, int subchan, int slice, packet_t pp, alevel_t alevel,
				     retry_t retries, fec_type_t fec_type)
{
	(void)chan; (void)subchan; (void)slice; (void)alevel; (void)retries;
	frames_out[fec_type == fec_type_fx25 ? 1 : 2]++;
	unsigned char out[AX25_MAX_PACKET_LEN];
	char addrs[AX25_MAX_ADDRS*AX25_MAX_ADDR_LEN];
	(void) ax25_pack (pp, out);
	ax25_format_addrs (pp, addrs);
	ax25_delete (pp);
}

int tq_count (int chan, int prio, char *source, char *dest, int bytes)
{
	(void)chan; (void)prio; (void)source; (void)dest; (void)bytes;
	return (0);
}

alevel_t demod_get_audio_level (int chan, int subchan)
{
	(void)chan; (void)subchan;
	alevel_t a;
	memset (&a, 0, sizeof(a));
	return (a);
}

void hex_dump (unsigned char *p, int len) { (void)p; (void)len; }

static void sendfun (int chan, int kiss_cmd, unsigned char *fbuf, int flen, struct kissport_status_s *onlykps, int onlyclient)
{
	(void)chan; (void)kiss_cmd; (void)fbuf; (void)flen; (void)onlykps; (void)onlyclient;
}


static void put_bits_lsb (void (*rec)(int,int,int,int), const uint8_t *p, int n)
{
	for (int i = 0; i < n; i++)
	  for (int k = 0; k < 8; k++)
	    rec (0, 0, 0, (p[i] >> k) & 1);
}

static void put_bits_msb (void (*rec)(int,int,int,int), const uint8_t *p, int n)
{
	for (int i = 0; i < n; i++)
	  for (int k = 7; k >= 0; k--)
	    rec (0, 0, 0, (p[i] >> k) & 1);
}


int LLVMFuzzerTestOneInput (const uint8_t *data, size_t size)
{
	static int init = 0;
	if ( ! init) {
	  text_color_init (0);
	  memset (&audio_config, 0, sizeof(audio_config));
	  audio_config.chan_medium[0] = MEDIUM_RADIO;
	  audio_config.achan[0].num_subchan = 1;
	  audio_config.achan[0].num_slicers = 1;
	  kiss_frame_init (&audio_config);
	  fx25_init (0);
	  il2p_init (0);
	  init = 1;
	}
	if (size < 2 || size > 4000) return 0;

	int sel = data[0] % 3;
	const uint8_t *p = data + 1;
	int n = (int)size - 1;

	if (sel == 0) {
	  static kiss_frame_t kf;		/* Kept between inputs, like a connection. */
	  for (int i = 0; i < n; i++) {
	    kiss_rec_byte (&kf, p[i], 0, NULL, -1, sendfun);
	  }
	}
	else if (sel == 1) {
	  int ctag = CTAG_MIN + p[0] % (CTAG_MAX - CTAG_MIN + 1);
	  uint64_t v = fx25_get_ctag_value (ctag);
	  uint8_t tag[8];
	  for (int k = 0; k < 8; k++) tag[k] = (v >> (8 * k)) & 0xff;
	  static const uint8_t flags[4] = { 0x7e, 0x7e, 0x7e, 0x7e };
	  put_bits_lsb (fx25_rec_bit, flags, 4);
	  put_bits_lsb (fx25_rec_bit, tag, 8);
	  put_bits_lsb (fx25_rec_bit, p + 1, n - 1);
	  put_bits_lsb (fx25_rec_bit, flags, 4);
	}
	else {
	  static const uint8_t sync[] = { 0x55, 0x55, 0x55, 0x55, 0xf1, 0x5e, 0x48 };
	  put_bits_msb (il2p_rec_bit, sync, sizeof(sync));
	  put_bits_msb (il2p_rec_bit, p, n);
	  static const uint8_t post[] = { 0x55, 0x55, 0x55, 0x55 };
	  put_bits_msb (il2p_rec_bit, post, sizeof(post));
	}
	return 0;
}


#ifdef MAKE_SEEDS

/*
 * Seed corpus: reads monitor format packets on stdin and writes, to the
 * directory given as the argument, KISS (selector 0) and IL2P (selector 2)
 * seeds.  FX.25 seeds come from the fxNN.dat files fxsend writes; see README.md.
 * Build like the fuzzer but with -DMAKE_SEEDS and without -fsanitize=fuzzer.
 */

#include <stdio.h>

static void put (const char *dir, int sel, int k, const unsigned char *b, int n)
{
	char fn[512];
	snprintf (fn, sizeof(fn), "%s/sel%d_%04d", dir, sel, k);
	FILE *fp = fopen (fn, "wb");
	if (fp == NULL) { perror (fn); exit (1); }
	unsigned char s = sel;
	fwrite (&s, 1, 1, fp);
	fwrite (b, 1, n, fp);
	fclose (fp);
}

int main (int argc, char *argv[])
{
	char line[3000];
	int k = 0;
	if (argc < 2) { fprintf (stderr, "usage: %s outdir < packets.txt\n", argv[0]); return 1; }
	text_color_init (0);
	il2p_init (0);
	while (fgets (line, sizeof(line), stdin) != NULL) {
	  line[strcspn (line, "\r\n")] = '\0';
	  if (line[0] == '#' || line[0] == '\0') continue;
	  packet_t pp = ax25_from_text (line, 1);
	  if (pp == NULL) continue;
	  unsigned char frame[AX25_MAX_PACKET_LEN+1], kiss[2*AX25_MAX_PACKET_LEN+10];
	  frame[0] = 0;					/* KISS: data frame, port 0 */
	  int flen = ax25_pack (pp, frame + 1);
	  int klen = kiss_encapsulate (frame, flen + 1, kiss);
	  put (argv[1], 0, k, kiss, klen);
	  unsigned char il2p[IL2P_MAX_PACKET_SIZE];
	  int ilen = il2p_encode_frame (pp, k & 1, il2p);
	  if (ilen > 0) put (argv[1], 2, k, il2p, ilen);
	  ax25_delete (pp);
	  k++;
	}
	/* KISS commands: TXDELAY, persist, slottime, txtail, full duplex, set hardware, return. */
	static const unsigned char cmds[][4] = { {0xc0,0x01,30,0xc0}, {0xc0,0x02,63,0xc0}, {0xc0,0x03,10,0xc0},
		{0xc0,0x04,5,0xc0}, {0xc0,0x05,1,0xc0}, {0xc0,0x06,0x00,0xc0}, {0xc0,0xff,0xc0,0xc0} };
	for (unsigned i = 0; i < sizeof(cmds)/sizeof(cmds[0]); i++) put (argv[1], 0, 9000 + i, cmds[i], 4);
	return 0;
}

#endif
