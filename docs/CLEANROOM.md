# Clean-room development policy

1. **No proprietary material in the repository**: no Nintendo/Actimagine SDK, no Mobiclip Multicore
   Encoder binaries or output, no game files, no `.moflex` samples (they are git-ignored).
2. **Sources of knowledge** (allowed): byte-level observation of files the developer legally owns,
   written format notes under `docs/`, public specifications and academic descriptions of generic
   techniques (DCT/intra prediction/entropy coding).
3. **Not allowed**: disassembling or decompiling the proprietary Mobiclip encoder/decoder to copy its
   algorithms; copying source from GPL/LGPL decoders (e.g. FFmpeg) into this MIT-licensed codebase.
   Existing open implementations may be read only to *confirm facts about the bitstream* which are
   then re-described in our own notes before being implemented independently; every such use is logged
   in `docs/PROVENANCE.md`.
4. Every format finding in `docs/` records its evidence (file, offsets) so it can be re-verified.
5. The tool never redistributes media; users operate on their own files.
