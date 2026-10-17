# MoflexTool

An open, dependency-free toolkit (library, CLI, Avalonia GUI) for inspecting, extracting,
editing and re-encoding **MOFLEX** (`.moflex`) video containers used on the Nintendo 3DS
(Mobiclip video + FastAudio / PCM audio).

Status: early analysis (see `docs/`).

## Clean-room policy
See [docs/CLEANROOM.md](docs/CLEANROOM.md). In short: no proprietary encoder/decoder code, SDK
files, keys or sample media are included; the format is documented from observation of
user-supplied files and public documentation, and every codec component is implemented from scratch.
