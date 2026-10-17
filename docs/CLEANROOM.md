# Development policy (copyright hygiene)

1. **No proprietary material in the repository**: no Nintendo/Actimagine SDK, no Mobiclip encoder
   binaries or output, no game files, no `.moflex` samples (they are git-ignored).
2. **Allowed sources of knowledge**: byte-level observation of files the developer legally owns,
   public specifications, public community documentation, and open-source implementations under a
   compatible license.
3. **Not allowed**: disassembling or decompiling proprietary Mobiclip encoders/decoders or game code.
   (The one game-specific fact found by experiment — the decoder hangs on the 9-bit escape code — was
   found by testing encoded files in an emulator, not by reading game code.)
4. **Open-source code**: the video decoder follows FFmpeg's LGPL-2.1+ `mobiclip.c` and its tables are
   transcribed from it. Because of that the whole project is licensed **LGPL-2.1-or-later** and the
   original authors are credited in `THIRD-PARTY-NOTICES.md` and in the source headers.
5. Every use of outside material is logged in `docs/PROVENANCE.md`; every format finding in `docs/`
   records how it can be re-verified (the `verify`/`remux` commands and the sample-based tests).
6. The tools never redistribute media; users operate on their own files.
