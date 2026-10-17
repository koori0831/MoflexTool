# MoflexTool

[English](README.md) | **한국어**

닌텐도 3DS 게임에서 쓰이는 Mobiclip 영상 형식 **MOFLEX**(`.moflex`)를 다루는 오픈소스 도구입니다.
독점 Mobiclip 인코더 없이 MOFLEX 파일을 디코딩·재인코딩·재구성할 수 있어서, 번역·모드 프로젝트에서
게임 속 영상을 일반 동영상으로 교체할 수 있습니다.

* **라이브러리** (`Moflex.Core`, .NET 10): 컨테이너 파서/먹서, Mobiclip 영상 디코더/인코더,
  FastAudio·IMA-ADPCM 오디오 디코더/인코더
* **CLI** (`moflex`): 사람과 AI·자동화 도구가 스크립트로 쓰기 좋은 명령
* **GUI** (`MoflexTool`, Avalonia): 몇 번의 클릭으로 정보 확인·추출·인코딩

## 지원 현황

| | 상태 |
|---|---|
| 컨테이너 읽기 | 테스트 게임의 영상 6개 모두 해석, 패킷 목록을 FFmpeg 디먹서와 대조해 확인 |
| 컨테이너 쓰기 | `remux`로 모든 샘플 파일을 **바이트 단위로 동일하게** 재생성 (sync 블록, 탐색 테이블, 타임스탬프, 인터리빙) |
| 영상 디코딩 | 시험한 샘플 파일에서 FFmpeg 출력과 완전히 동일 (I/P 프레임) |
| 영상 인코딩 | 키프레임과 예측 프레임(반화소 움직임 탐색, 다중 참조, 인트라 대체), 2D 및 입체 3D(좌우 프레임 교차) |
| 오디오 | IMA-ADPCM(MOFLEX 변형)·FastAudio 디코딩(FFmpeg와 비트 단위 일치)·인코딩, 16비트 PCM |
| 인게임 | *디지몬 월드 리:디지타이즈 디코드*에서 재인코딩한 42초 컷신이 Azahar 에뮬레이터에서 끝까지 재생됨 |

아직 미지원: DS `.mods` / Wii `.mo`. FastAudio는 FFmpeg와만 대조했고, 원본 FastAudio 영상이나 게임에서는
시험하지 않았습니다. 인코딩 속도는 400×240 기준
CPU 코어 1개당 초당 약 50프레임이고 키프레임 구간을 모든 코어에서 병렬로 인코딩하며(`--threads N`으로 제한),
실제 3DS 기기에서는 아직 시험하지 않았습니다.

## 빌드

[.NET 10 SDK](https://dotnet.microsoft.com/)가 필요합니다.

```bash
dotnet build -c Release
dotnet test
dotnet publish src/Moflex.Cli -c Release -o publish/cli
dotnet publish src/Moflex.Gui -c Release -o publish/gui
```

mp4, mkv 같은 일반 영상을 인코딩하려면 [FFmpeg](https://ffmpeg.org/)를 `PATH`에 두거나 `--ffmpeg <경로>`로
지정하세요. FFmpeg는 입력 영상을 디코딩·크기 조정하는 외부 프로그램으로만 쓰입니다.

## CLI

```bash
moflex info movie.moflex                       # 스트림, 프레임 레이트, 3D 레이아웃, 크기
moflex export-video movie.moflex movie.avi     # 소리까지 담긴 무손실 영상 파일 하나 (바로 편집 가능)
moflex export-audio movie.moflex movie.wav     # 오디오만 디코딩
moflex export-video movie.moflex movie.yuv     # 영상만 디코딩 (raw yuv420p, 저장된 모든 프레임)
moflex verify movie.moflex                     # 먹서가 파일을 똑같이 재현하는지, 모든 프레임이 디코딩되는지
moflex remux movie.moflex rebuilt.moflex       # 먹서로 파일 재구성
```

`.avi`나 `.mov`로 `export-video`를 하면 내장 디코더로 화면을 디코딩해 무손실로 저장합니다(기본 Ut Video,
`--codec ffv1|magicyuv|rawvideo`로 변경 가능). 소리는 16비트 PCM이고, FFmpeg는 컨테이너를 쓰는 데만 쓰입니다.
3D 영상은 좌우 나란히(SBS) 한 화면(왼쪽 절반 = 왼쪽 눈)이 되고, `--eye left|right`로 한쪽 눈만 뽑을 수 있습니다.
이 SBS 파일을 3D 영상을 `--like`로 주고 인코딩하면 자동으로 좌/우 프레임으로 돌아갑니다.

### 게임 영상 교체

원본 영상을 기준 파일로 넘기면 새 파일이 원본의 크기, 프레임 레이트, 3D 레이아웃, 오디오 형식을 그대로
따라가므로 게임은 같은 종류의 스트림을 받게 됩니다.

```bash
moflex encode edited.mp4 -o inGame1.moflex --like original/inGame1.moflex -q 18 --pq 22
```

* 원본이 3D일 때 일반 2D 영상은 양쪽 눈에 같은 화면으로 들어갑니다.
* 좌우 나란히(SBS) 3D 영상(왼쪽 절반 = 왼쪽 눈)은 `--sbs`를 주면 진짜 좌/우 프레임으로 인코딩됩니다. 기준 영상이
  3D이고 입력 폭이 한쪽 눈의 두 배면(`export-video`로 뽑은 파일처럼) 자동으로 그렇게 처리합니다.
* `-q`(키프레임, 12~63)와 `--pq`(예측 프레임, 12~161)는 낮을수록 고화질·대용량입니다.

기준 파일 없이: `moflex encode in.mp4 -o out.moflex --size 400x240 --fps 30000/1001`
(3D 레이아웃 파일이 필요하면 `--stereo duplicate` 추가). 전체 옵션은 `moflex --help`를 보세요.

## GUI

`MoflexTool`은 두 개의 탭으로 되어 있습니다.
**Inspect / Export**(`.moflex` 열기, 재생·일시정지·탐색 미리보기, 무손실 AVI/MOV 또는 WAV/YUV 추출, 검증)와
**Encode**(입력 영상, 기준 영상(선택), 화질, SBS 옵션, 진행률).

## 개발 방식

컨테이너는 개발자가 소유한 게임 파일을 분석해서 밝혔고, [docs/FORMAT-MOFLEX.md](docs/FORMAT-MOFLEX.md)의
모든 규칙은 원본 파일을 바이트 단위로 재생성하는 것으로 검증했습니다. 영상 코덱은 FFmpeg의 오픈소스
Mobiclip 디코더를 따르며, 인코더는 디코더를 복원 루프에 재사용하는 독자 구현입니다. 참고한 자료와 사용
범위는 [docs/PROVENANCE.md](docs/PROVENANCE.md), 원칙은 [docs/CLEANROOM.md](docs/CLEANROOM.md)에 있습니다.

직접 인코더를 만든다면 알아 둘 점: run/level 코드표에 이스케이프 기호가 두 번 들어 있는데, 게임의
디코더는 9비트 채움용 코드를 만나면 멈춥니다. 항상 7비트 이스케이프 코드를 쓰세요.

## 테스트

`dotnet test`는 합성 영상으로 인코더/디코더 왕복 테스트를 실행합니다. 가지고 있는 영상으로 바이트 단위
컨테이너 테스트까지 돌리려면 `MOFLEX_SAMPLES`에 `.moflex` 파일 폴더를 지정하세요.

```bash
MOFLEX_SAMPLES=/path/to/movies dotnet test
```

## 법적 고지

* MoflexTool에는 닌텐도·Actimagine의 코드, SDK 파일, 키, 미디어가 들어 있지 않으며 독점 인코더를
  디스어셈블하지 않았습니다. 수정할 권리가 있는 파일에만 사용하세요.
* Nintendo 3DS는 닌텐도의 상표입니다. 이 프로젝트는 닌텐도나 Actimagine과 관련이 없습니다.

## 크레딧과 라이선스

* FFmpeg Mobiclip 디코더 — Florian Nouwt, Adib Surani, Paul B Mahol (디코더와 표의 바탕)
* "The Mobiclip Formats" — quatric (mobipeg 프로젝트), 컨테이너 사실 교차 확인에 사용
* Avalonia UI, xUnit.net, .NET

자세한 내용: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

**GNU LGPL v2.1 이상**으로 배포됩니다 ([LICENSE](LICENSE)).
