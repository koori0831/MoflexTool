# Provenance log
| Date | Finding | Evidence / source |
|---|---|---|
| 2026-10-17 | MOFLEX container layout (sync header, descriptors, bit-packed chunk headers) | Observed in sample files; cross-checked with FFmpeg libavformat/moflex.c (LGPL) read-only; re-described independently |
| 2026-10-17 | Mobiclip video bitstream syntax (planned) | FFmpeg libavcodec/mobiclip.c (LGPL) read for syntax facts; tables are interoperability data |
