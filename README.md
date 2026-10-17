# MoflexTool

**English** | [한국어](README.ko.md)

An open-source toolkit for **MOFLEX** (`.moflex`), the Mobiclip video format used by Nintendo 3DS games.
It decodes, re-encodes and re-muxes MOFLEX files without the proprietary Mobiclip encoder, so translation
and mod projects can replace in-game movies with ordinary video files.

* **Library** (`Moflex.Core`, .NET 10): container parser and muxer, Mobiclip video decoder and encoder,
  IMA-ADPCM audio decoder and encoder.
* **CLI** (`moflex`): scriptable commands for people and for AI/automation tools.
* **GUI** (`MoflexTool`, Avalonia): inspect / export / encode with a few clicks.

## What works

| | Status |
|---|---|
| Container parsing | All six movies of the test game parse; packet list checked against FFmpeg's demuxer |
| Container writing | `remux` rebuilds every sample file **byte for byte** (sync blocks, seek table, time stamps, interleaving) |
| Video decoding | Output identical to FFmpeg on the tested sample files (I and P frames) |
| Video encoding | Key frames and predicted frames (half-pel motion, multi-reference, intra fallback); 2D and stereoscopic 3D (interleaved left/right) |
| Audio | IMA-ADPCM (MOFLEX variant) decode, bit-exact with FFmpeg; encode; 16-bit PCM |
| In-game | A re-encoded 42 s cut-scene played to the end in the Azahar emulator inside *Digimon World Re:Digitize Decode* |

Not yet supported: FastAudio (audio codec 0), DS `.mods` / Wii `.mo`. Encoding speed is modest
(about 8 frames/s at 400×240 on one CPU core), and it has not been tested on real 3DS hardware yet.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/).

```bash
dotnet build -c Release
dotnet test
dotnet publish src/Moflex.Cli -c Release -o publish/cli
dotnet publish src/Moflex.Gui -c Release -o publish/gui
```

To encode arbitrary videos (mp4, mkv, …) put [FFmpeg](https://ffmpeg.org/) on `PATH` or pass `--ffmpeg <path>`.
FFmpeg is only used as an external program to decode and scale the input.

## CLI

```bash
moflex info movie.moflex                       # streams, frame rate, 3D layout, sizes
moflex export-audio movie.moflex movie.wav     # decode audio
moflex export-video movie.moflex movie.yuv     # decode video (raw yuv420p, every stored frame)
moflex verify movie.moflex                     # muxer reproduces the file? all frames decode?
moflex remux movie.moflex rebuilt.moflex       # rebuild the file with the muxer
```

### Replacing a game movie

Give the original movie as the reference: the new file copies its size, frame rate, 3D layout and audio
format, so the game sees the same kind of stream.

```bash
moflex encode edited.mp4 -o inGame1.moflex --like original/inGame1.moflex -q 18 --pq 22
```

* A normal 2D video is shown identically to both eyes when the original is 3D.
* A side-by-side 3D video (left half = left eye) becomes real left/right frames with `--sbs`.
* `-q` (key frames, 12–63) and `--pq` (predicted frames, 12–161): lower is better quality and bigger.

Without a reference: `moflex encode in.mp4 -o out.moflex --size 400x240 --fps 30000/1001`
(add `--stereo duplicate` for a 3D-layout file). Run `moflex --help` for every option.

## GUI

`MoflexTool` has two tabs:
**Inspect / Export** (open a `.moflex`, show its streams, export WAV/YUV, verify) and
**Encode** (input video, optional reference movie, quality, side-by-side option, progress).

## How it was built

The container was analysed from the developer's own game files: every rule in
[docs/FORMAT-MOFLEX.md](docs/FORMAT-MOFLEX.md) is confirmed by regenerating the original files byte for byte.
The video codec follows FFmpeg's open-source Mobiclip decoder; the encoder is original work that reuses the
decoder for closed-loop reconstruction. Sources and their use are listed in
[docs/PROVENANCE.md](docs/PROVENANCE.md) and the rules in [docs/CLEANROOM.md](docs/CLEANROOM.md).

One finding worth knowing if you write your own encoder: the run/level code table contains the escape symbol
twice, and the game's decoder hangs on the 9-bit filler code — always use the 7-bit escape code.

## Tests

`dotnet test` runs synthetic encoder/decoder round-trip tests. To also run the byte-exact container tests on
your own movies, point `MOFLEX_SAMPLES` at a folder of `.moflex` files:

```bash
MOFLEX_SAMPLES=/path/to/movies dotnet test
```

## Legal

* MoflexTool contains no Nintendo or Actimagine code, SDK files, keys or media. The proprietary encoder was
  not disassembled. Use it only with files you are allowed to modify.
* Nintendo 3DS is a trademark of Nintendo. This project is not affiliated with Nintendo or Actimagine.

## Credits and license

* FFmpeg Mobiclip decoder by Florian Nouwt, Adib Surani and Paul B Mahol — the basis of the decoder and tables.
* "The Mobiclip Formats" by quatric (mobipeg project) — used to cross-check container facts.
* Avalonia UI, xUnit.net, .NET.

Details: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Licensed under the **GNU Lesser General Public License v2.1 or later** ([LICENSE](LICENSE)).
