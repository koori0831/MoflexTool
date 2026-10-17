# MOFLEX container

All integers big-endian unless stated. Everything below was derived from user-owned `.moflex` files
(3DS game *Digimon World Re:Digitize Decode*), checked against the public behaviour of the FFmpeg
`moflex` demuxer, and cross-checked with the public community write-up listed in PROVENANCE.md.
`MoflexMuxer` regenerates **byte-identical** files from the packets of all six DWRD movies, which is the
acceptance test for this document (`moflex remux` + `cmp`).

## File layout
A sequence of **blocks** of at most the declared block size (4096 in every sample; 2048-byte blocks are
reported to crash real hardware). A block starts with an optional sync header, a flags byte and then
chunks. A frame may be split into several chunks spread over consecutive blocks.

### Sync header
| offset | size | meaning |
|---|---|---|
| 0 | 2 | magic `4C 32` ("L2") |
| 2 | 2 | check field = `(ts & 0xFFFF) ^ ((ts >> 16) & 0xFFFF) ^ … ^ 0xAAAA` (fold of the 64-bit timestamp) |
| 4 | 8 | timestamp of the segment in microseconds |
| 12 | 2 | block size − 1 |
| 14 | … | stream descriptors, terminated by a type-0 descriptor (`00 00`) |

Descriptor = `type` (var-int) `size` (var-int) `payload[size]`:

| type | meaning | payload |
|---|---|---|
| 0 | end of descriptors | `size` bytes skipped (0) |
| 1, 3 | video | index u8, codec u8 (0 = Mobiclip), frame-duration denominator u16, numerator u16, width u16, height u16, 2 unknown bytes (`01 01`), type 3 only: layout byte |
| 2 | audio | index u8, codec u8 (0 FastAudio, 1 IMA-ADPCM-MOFLEX, 2 PCM s16le), sample_rate−1 u24, channels−1 u8 |
| 4 | data | index u8, 1 reserved byte (`00`) — carries the seek table |

Video layout byte (type 3): `layout | rotation << 4`, layout 0 = interleaved 3D left-first, 1 = interleaved
right-first, 2/3 = top-bottom, 4/5 = side-by-side, 6 = flat 2D. In interleaved 3D the two eyes are
**consecutive frames of the one video stream that share a time stamp** (the DWRD movies are 400x240 at
59.83 fps stored = 29.9 stereo pairs/s).

### Flags byte
`flags = counter << 2 | 1`. The counter is 6 bits, belongs to the current sync segment and is the same
in every block of the segment. It starts at 3 for the first sync block and increments with every sync
block, wrapping `63 → 1`. The file's very first block (header + seek table) carries the value the counter
would take **after** the last sync block. Bit 1 (two extra bytes) never occurs in the samples.

### Chunk header — MSB-first bit stream
1. `len` = number of bits up to and including the first `1` bit (unary); `stream` = next `len` bits
   (minimal length for the value).
2. `end` flag (1 bit): this chunk completes a packet.
3. If `end`: `len1`(unary) `value1`(`len1` bits), `sign` (1 bit), `len2`(unary) `value2`(`2*len2+26` bits).
   * `value2` / `sign`: packet time in µs **relative to the current sync segment**; `sign = 1` means negative
     (audio may start slightly before the key frame that opens a segment). `len2` is the smallest value that
     fits (always 1 in practice).
   * `value1`: 0 for the key frame that starts a sync segment, 1 for every other video packet, 0 for audio
     and for the data packet.
4. `size − 1` in 13 bits; then padding to a byte boundary, then `size` payload bytes.

A zero byte where a chunk header would start terminates the block's chunk list; the next block follows
immediately (no padding to the block size).

## Layout rules (as produced by the original encoder)
* **Header block**: sync header (ts = 1), flags, one chunk for the data stream (the seek table), zero byte.
* **Sync block**: every video key frame starts a new block with a sync header whose timestamp equals the
  frame time. The block contains *only* that frame; when the frame ends before the block is full a zero byte
  terminates the block. All other packets then start a fresh block (same counter, no header).
* Non-key packets are written back to back and **split greedily across blocks** so that every block
  is completely filled (4096 bytes).
* **Time stamps** (absolute, µs, files start at 1):
  * video frame `f`: `floor(⌊f / g⌋ · g · num · 10⁶ / den) + 1` with `g` = 2 for interleaved 3D (two frames share a
    stamp) else 1, `num/den` = frame duration from the descriptor;
  * audio packet: `floor(samples_before · 10⁶ / rate) + 1`.
* **Interleaving**: packets are merged by time; a video packet is written before an audio packet if its
  time is ≤ the audio time + ~one frame duration (empirically 2673 ≤ lead < 31604 µs; 16714 reproduces all files).
* **Audio packet size** (ADPCM): one packet per time-stamp group, boundaries at
  `round(i · rate · group_duration / 256) · 256` samples.

## Seek table (data stream payload, little-endian)
```
u32 n            number of sync points
u32 total_frames number of video frames
u32 duration_us  total duration
u32 0
n × { u64 frame_index, u64 time_us, u64 file_offset_of_sync_block }
```
Entries list the sync blocks in file order (the first entry is the frame-0 sync block at the end of the header block).

## Audio packet (IMA-ADPCM MOFLEX)
Per channel 4 header bytes (step index s16le, predictor s16le), then 256-sample sub-frames of 128 bytes per
channel (low nibble first), channels in turn inside a sub-frame. Duration = `size / (ch · 132) · 256` samples.

## Audio packet (FastAudio, codec 0)
Not used by the DWRD movies. The syntax below follows FFmpeg's `fastaudio` decoder; `FastAudioDecoder` produces
**bit-identical float output** to FFmpeg (checked on files written by `FastAudioEncoder` and on pseudo-random
packets, see `FastAudioTests.DecoderMatchesFfmpegBitExactly`). It has not been compared with files made by the
original encoder, because none of the sample movies use it.

A packet is a whole number of **sub-frames**; one sub-frame is 256 samples of one channel in 40 bytes, and
channels alternate inside each sub-frame time slot (`sf0 ch0, sf0 ch1, sf1 ch0, …`). Duration =
`size / (40 · ch) · 256` samples. Packets are cut at the same time-stamp-group boundaries as ADPCM.
The decoder state (filter memories) carries over from packet to packet; there is no per-packet header.

### Sub-frame bit layout
The 40 bytes are ten **little-endian 32-bit words**; fields are read **MSB first** from word 0 onward and no
field crosses a word boundary.

| word | fields (in read order) |
|---|---|
| 0 | coefficient indices `c0` 6, `c1` 6, `c2` 5, `c3` 5, `c4` 4, `c6` 3, `c7` 3 bits (`c5` has no field here) |
| 1 | gain indices of groups 3, 2, 1, 0 (6 bits each), grid phases of groups 3, 2, 1, 0 (2 bits each) |
| 2–9 | two words per 64-sample group 0…3: pulses p0…p9 (3 bits each), `t1` 2 bits, p10…p19 (3 bits each), `t2` 2 bits |

* Pulse 20 of a group is `(4·t1 + t2) >> 1`; the low bit of `t2` of groups 0…3 (group 0 = MSB) forms the
  4-bit index `c5`.
* Excitation of group g: zero except samples `64g + phase + 3j` (j = 0…20) = `gain · (2·p_j − 7)`, where
  `gain` = the float with bit pattern `(gain_index + 1) << 20`, times 2^116 (≈ 2^-13 … 2^-3, ⅛-octave steps).

### Synthesis
Coefficient index `ci` selects `m[7 − i] = T_i[ci]` from fixed quantiser tables (`FastAudio.BuildTables`;
T0/T1 are dense near ±1, T2–T7 have 32/32/16/16/8/8 entries). For every sample the excitation `x` runs
through an 8-stage all-pole lattice and a de-emphasis:

```
for j in 0..7: x -= m[j]·f[j];  f[j] += m[j]·x
f = f[1..7] ++ [x]                      (f[7] is the newest output)
last = x + 0.86·last;  out = 2·last
```

`m[8 − i]` is the reflection coefficient k_i of a predictor `A(z) = 1 + Σ a_i z^-i` in the usual
Levinson–Durbin convention (`A_i(z) = A_{i-1}(z) + k_i z^-i A_{i-1}(1/z)`), so the lattice equals `1/A(z)`
(checked by `FastAudioTests.LatticeMatchesDirectFormAllPoleFilter`).

### Encoder (MoflexTool's own design)
Per sub-frame: autocorrelation of the pre-emphasised input (`x[n] − 0.86·x[n−1]`) over a 384-sample Hann
window centred on the sub-frame, 0.3 % noise floor and a small lag window, Levinson–Durbin, nearest table
entry per coefficient. Per 64-sample group: analysis by synthesis against the decoder's own filter
(zero-input response removed), least-squares pulse amplitudes for each of the four grid phases, then a few
gain candidates with coordinate-descent refinement of the 3-bit levels; the decoder state is advanced with the
chosen excitation so the encoder never drifts. About 19 dB SNR on game speech/music at 47 864 Hz (60 kbit/s
per channel, a third of IMA-ADPCM's size), roughly 8× real time on one core.

## Video packet (Mobiclip)
The first bit of the packet *after swapping each 16-bit word* is the key-frame flag (not the first payload byte).

## Encoder pitfall: duplicate escape code
The run/level code tables contain symbol 0 (the escape) twice: a 9-bit filler code and the 7-bit code that
real streams use. FFmpeg decodes both, but the game's decoder hangs on the 9-bit one, so encoders must emit
the 7-bit code (found by testing in Azahar; all original streams use only the 7-bit code).
