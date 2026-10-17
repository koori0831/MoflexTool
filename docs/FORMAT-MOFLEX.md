# MOFLEX container (observed + confirmed)

All integers big-endian unless stated. Facts below were observed in user-owned `.moflex` files
(3DS game *Digimon World Re:Digitize Decode*) and confirmed against the public behaviour of the
FFmpeg `moflex` demuxer (see PROVENANCE.md). They are re-described here in our own words.

## File layout
The file is a sequence of **blocks** of a fixed size (declared in the first header, 4096 bytes in
every sample). Packets never cross a block boundary but a *frame* may be split into several
chunks that are spread over consecutive blocks.

### Sync header (`L2` header)
Present at the very start of the file (and may be repeated at the start of any block).

| offset | size | meaning |
|---|---|---|
| 0 | 2 | magic `4C 32` ("L2") |
| 2 | 2 | `AA AB` (ignored by readers) |
| 4 | 8 | timestamp of this block (u64) – `0 … 1` in samples |
| 12 | 2 | block size − 1 |
| 14 | … | stream descriptors, terminated by a type-0 descriptor |

Descriptor = `type` (var-int) `size` (var-int) `payload[size]`:

| type | meaning | payload |
|---|---|---|
| 0 | end of descriptors | `size` bytes skipped (usually 0) |
| 1, 3 | video stream | index u8, codec u8 (0 = Mobiclip), timebase-den u16, timebase-num u16, width u16, height u16, then 2 (type 1) or 3 (type 3) reserved bytes |
| 2 | audio stream | index u8, codec u8 (0 FastAudio, 1 IMA-ADPCM-MOFLEX, 2 PCM s16le), sample_rate−1 u24, channels−1 u8 |
| 4 | data stream | index u8, 1 reserved byte |

var-int: 7-bit groups, MSB set = another byte follows; at most 4 bytes (the 4th byte uses all 8 bits).

### Block body
After the (optional) sync header: `flags` u8 (bit 1 = two extra bytes follow, bit 0 = packets of
the block fill the block exactly, otherwise the reader skips to the block end).
Then packets until the block ends or a zero byte is found where a packet header would start.

### Packet (chunk) header — MSB-first bit stream
1. `len` = number of bits up to and including the first `1` bit (unary); `stream` = next `len` bits.
2. `end` flag (1 bit). `end = 1` means this chunk completes a frame/packet of the stream.
3. If `end`: extra fields (timing, partly unknown): `len1`(unary) value(`len1` bits), 1 bit,
   `len2`(unary) value(`2*len2+26` bits).
4. `size − 1` in 13 bits.
The header is padded to a byte boundary, followed by `size` payload bytes.
Chunks of the same stream are concatenated until a chunk with `end = 1` arrives.

Video packet: first payload bit set ⇒ keyframe.

(Work in progress: meaning of the timing fields, the data-stream index table, muxing rules.)

## Findings (timing, sync headers)
- `Value2` of the end-info is a microsecond timestamp. Video packets come in pairs sharing a stamp (33427/33428 µs per pair); audio stamps are cumulative (32090 µs per 1536-sample packet at 47864 Hz).
- Sync headers may repeat mid-stream (e.g. at offset 212 and 160413 in inGame5). Their bytes 2-3 differ from the first one (`1E DC` vs `AA AB`) and carry a different timestamp, so they are copied verbatim when re-muxing.
- Flags byte: bit0 = next block follows immediately (no padding to block size), bit1 = 2 extra bytes follow; other bits (0x08/0x10/0x20/0x04) are not yet understood.
- Re-writing the parsed layout (header bytes, flags, chunks, terminator, padding) is byte-exact for all six DWRD movies.
