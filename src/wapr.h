/*
 * WAPR - Weak-signal Adaptive Packet Radio.  EXPERIMENTAL.
 *
 * A research modem, not compatible with AX.25 / APRS radios on the air.
 * See doc/wapr/ for the design, the frame format and measured results.
 *
 * wapr_codec.c:	frame format, CRC, scrambler, LDPC code, interleaver, symbols.
 * wapr_modem.c:	GFSK M-FSK modulator and receiver.
 */

#ifndef WAPR_H
#define WAPR_H 1

#define WAPR_VERSION		0

#define WAPR_HEADER_BYTES	12
#define WAPR_PAYLOAD_AREA	32	/* Fixed size class, no fragmentation. */
#define WAPR_INFO_BYTES		(WAPR_HEADER_BYTES + WAPR_PAYLOAD_AREA)
#define WAPR_INFO_BITS		(WAPR_INFO_BYTES * 8)
#define WAPR_CRC_BITS		24
#define WAPR_MSG_BITS		(WAPR_INFO_BITS + WAPR_CRC_BITS)	/* LDPC K */
#define WAPR_CODE_BITS		(2 * WAPR_MSG_BITS)			/* LDPC N, rate 1/2 */

#define WAPR_MAX_TONES		8
#define WAPR_MAX_SYMBOLS	(WAPR_CODE_BITS / 2 + 8 * WAPR_MAX_TONES)

#define WAPR_ADDR_LEN		10	/* "ZZZZZZ-15" + nul */

/* How WAPR frames look to the rest of Dire Wolf (AX.25 packet form). */

#define WAPR_BROADCAST_TOCALL	"APZWAP"	/* experimental tocall used as AX.25 destination */
#define WAPR_RELAY_MARK		"WAPRGW"	/* path of a frame relayed by a gateway (type 2) */

enum wapr_type_e { WAPR_TYPE_RAW = 0, WAPR_TYPE_APRS = 1, WAPR_TYPE_APRS_RELAYED = 2, WAPR_TYPE_LINK_ACK = 3 };
				/* RELAYED: put on WAPR by a gateway; never gated again. */
				/* LINK_ACK: "source received dest's frame seq"; no payload, never delivered. */

typedef struct wapr_frame_s {
	int type;			/* enum wapr_type_e */
	int ack;			/* acknowledgement requested */
	int seq;			/* 0 .. 1023 */
	char source[WAPR_ADDR_LEN];	/* callsign[-ssid] */
	char dest[WAPR_ADDR_LEN];	/* "" for broadcast */
	int len;			/* 0 .. WAPR_PAYLOAD_AREA */
	unsigned char payload[WAPR_PAYLOAD_AREA];
} wapr_frame_t;

/* Errors from pack / unpack / decode, all negative. */

enum wapr_err_e {
	WAPR_OK = 0,
	WAPR_ERR_VERSION = -1,
	WAPR_ERR_TYPE = -2,
	WAPR_ERR_FLAGS = -3,
	WAPR_ERR_LENGTH = -4,
	WAPR_ERR_SEQ = -5,
	WAPR_ERR_ADDRESS = -6,
	WAPR_ERR_PADDING = -7,
	WAPR_ERR_FEC = -8,		/* LDPC decoder did not converge */
	WAPR_ERR_CRC = -9
};

const char *wapr_strerror (int err);

int wapr_frame_pack (const wapr_frame_t *f, unsigned char info[WAPR_INFO_BYTES]);
int wapr_frame_unpack (const unsigned char info[WAPR_INFO_BYTES], wapr_frame_t *f);

/*
 * Physical layer profile.  Frozen parameters of a stage 1 candidate;
 * see test/wapr/configs/stage1.json.
 */

typedef struct wapr_profile_s {
	const char *name;
	int tones;		/* M: 4 or 8 */
	double baud;		/* symbols per second */
	double f_center;	/* audio frequency of the middle of the tone set, Hz */
	double bt;		/* Gaussian filter BT, 0 for plain CPFSK */
	int sync_blocks;	/* Costas blocks: start, end, evenly in between */
	double freq_search;	/* receiver searches +- this many Hz */
	int amp_window;		/* symbols used to track signal amplitude for soft bits */
} wapr_profile_t;

const wapr_profile_t *wapr_profile_find (const char *name);

/* Symbol layout derived from a profile. */

typedef struct wapr_layout_s {
	int bits_per_sym;
	int n_data;		/* data symbols */
	int n_sym;		/* all symbols */
	int costas[WAPR_MAX_TONES];
	unsigned char is_sync[WAPR_MAX_SYMBOLS];	/* 0 data, 1 sync */
	unsigned char sync_tone[WAPR_MAX_SYMBOLS];	/* valid where is_sync */
} wapr_layout_t;

void wapr_layout (const wapr_profile_t *p, wapr_layout_t *lay);

/* Information block (header + payload area) to the tone sequence.  Returns symbol count. */

int wapr_encode (const wapr_profile_t *p, const unsigned char info[WAPR_INFO_BYTES], unsigned char *syms);

/*
 * Soft decision decoding.  llr[] holds one value per transmitted code bit,
 * in transmitted order, positive meaning 0 is more likely.
 * Returns WAPR_OK and the information block, or WAPR_ERR_FEC / WAPR_ERR_CRC.
 */

int wapr_decode (const float llr[WAPR_CODE_BITS], unsigned char info[WAPR_INFO_BYTES], int *iterations);

/* Lower level, exposed for tests. */

unsigned int wapr_crc24 (const unsigned char *bits, int nbits);
void wapr_ldpc_encode (const unsigned char msg[WAPR_MSG_BITS], unsigned char code[WAPR_CODE_BITS]);
int wapr_ldpc_decode (const float llr[WAPR_CODE_BITS], unsigned char msg[WAPR_MSG_BITS], int max_iter);


/* wapr_modem.c */

/* Audio for a tone sequence: amplitude amp, sample rate fs.  Returns sample count. */

int wapr_modulate (const wapr_profile_t *p, const unsigned char *syms, int nsym, int fs, float amp, float *out, int maxout);

int wapr_samples_needed (const wapr_profile_t *p, int fs);

typedef struct wapr_rx_result_s {
	wapr_frame_t frame;
	double start;		/* sample index of the first symbol */
	double freq_offset;	/* Hz */
	double sync_metric;
	float snr_est;		/* rough SNR_2500 estimate, dB */
} wapr_rx_result_t;

/*
 * Search an audio buffer for frames and decode them.
 * Returns the number of frames written to res[] (at most max_res).
 * Candidates tried are counted in *tried if not NULL.
 */

int wapr_receive (const wapr_profile_t *p, const float *x, int n, int fs, wapr_rx_result_t *res, int max_res, int *tried);


/*
 * Channel busy detection for a WAPR channel: in-band energy above a tracked noise
 * floor.  Cheap enough for the audio thread.  Signals below the noise in the band
 * (which WAPR can still decode) are not seen: this only avoids transmitting over
 * clearly audible signals.
 */

typedef struct wapr_dcd_s {
	double b0, b1, b2, a1, a2;	/* band pass biquad */
	double x1, x2, y1, y2;
	double fast, floor;		/* in-band power, noise floor */
	double af, adown, aup;		/* smoothing constants */
	long warmup, busy_for, max_busy;
	int busy;
} wapr_dcd_t;

void wapr_dcd_init (wapr_dcd_t *d, const wapr_profile_t *p, int fs);

/* One sample.  Returns +1 when the channel becomes busy, -1 when it becomes clear, else 0. */
int wapr_dcd_sample (wapr_dcd_t *d, int sam);

#endif
