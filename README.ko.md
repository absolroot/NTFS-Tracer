# NTFS Tracer

<h3 align="center">별도 추출 도구 없이 디스크 이미지에서 바로 NTFS 로그를 분석하세요.</h3>

<p align="center"><strong>Windows 앱 하나에서 파일 이름을 검색하고, 이름 변경·이동·삭제 이력을 원본 기록까지 추적할 수 있습니다.</strong></p>

<p align="center">USN 저널 분석을 시작해 첫 파일의 이전 이름을 찾기까지 <strong>NTFS Log Tracker 1.9보다 8배 이상 빨랐습니다</strong>.</p>

<p align="center">
  <a href="https://github.com/absolroot/NTFS-Tracer/releases/latest"><img src="https://badgen.net/github/release/absolroot/NTFS-Tracer/latest" alt="최신 릴리스"></a>
  <a href="https://www.codefactor.io/repository/github/absolroot/ntfs-tracer/issues/main"><img src="https://www.codefactor.io/repository/github/absolroot/ntfs-tracer/badge/main" alt="CodeFactor 코드 품질 등급"></a>
  <a href="https://github.com/absolroot/NTFS-Tracer/actions/workflows/ci.yml"><img src="https://github.com/absolroot/NTFS-Tracer/actions/workflows/ci.yml/badge.svg?branch=main&amp;event=push" alt="Windows 빌드 및 테스트"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/absolroot/NTFS-Tracer?color=blue" alt="Apache 2.0 라이선스"></a>
</p>

[Windows x64·x86 다운로드](https://github.com/absolroot/NTFS-Tracer/releases/latest) · [English](README.md) · [日本語](README.ja.md)

## 왜 NTFS Tracer를 쓰나요?

- 디스크 이미지에서 `$J`, `$MFT`, `$LogFile`을 바로 추출해 분석할 수 있습니다.
- 현재 이름이나 이전 이름으로 파일을 검색하고, 이름 변경 전후의 USN 기록과 원본 바이트를 확인할 수 있습니다.
- 저장한 조사를 다시 열면 검색 조건과 선택한 기록이 복원됩니다.

## 시작하기

### 앱에서 조사하기

64비트 Windows에서는 x64 ZIP을, 32비트 Windows에서는 x86 ZIP을 내려받으세요. 압축을 풀고 `NtfsTracer.exe`를 실행하세요. 경량판에는 ZIP과 같은 아키텍처(x64 또는 x86)의 .NET 10 Windows Desktop Runtime이 필요합니다.

1. 처음 실행하면 **설정** 창이 열립니다. 자료에 맞는 방법으로 분석을 시작하세요.
   - **이미 추출한 자료가 있으면** `$J`, `$MFT`, `$LogFile` 또는 이 파일들이 들어 있는 폴더를 선택하고 **분석 시작**을 누르세요. 개별 파일이나 폴더를 창에 끌어 놓을 수도 있습니다.
   - **디스크 이미지가 있으면** 이미지 파일을 선택하세요. 읽을 수 있는 첫 번째 NTFS 볼륨이 자동으로 선택됩니다. 분석할 볼륨과 추출할 항목을 확인한 뒤 **추출 후 분석**을 누르세요.
2. **전체 로그**에서 파일을 검색하세요. 결과에서 파일 이력을 열면 이전 이름과 경로를 확인할 수 있습니다. `$LogFile`을 열었다면 **NTFS 트랜잭션** 탭에서 기록을 조회할 수 있습니다.
3. 기록을 선택하면 개요에 변경 사유, 경로를 확인한 근거, 원본 파일 내 위치가 표시됩니다. `F1`을 누르면 사용 안내가 열립니다.

작업을 중단할 때는 **파일 → 조사 저장…**을 선택하세요. 다음 실행 때 **마지막 조사 이어서 열기**를 선택하면 저장한 자료와 검색 조건, 선택한 기록으로 돌아갈 수 있습니다.

<a id="export-usn-to-csv"></a>

### USN 기록을 CSV로 내보내기

배포 ZIP을 압축 해제한 폴더에서 아래 명령을 실행하세요. USN V2 기록만 담긴 `$J`를 입력하고, 출력 파일에는 새 이름을 지정하세요.

```powershell
.\ntfstracer-cli.exe usn-csv '.\$J' '.\usn.csv'
```

## 조사 기능

[![NTFS Tracer 전체 창에서 저널 검색, 파일 이력, 트랜잭션, 조사 후보를 차례로 확인하는 데모](docs/media/full-app-walkthrough-dark.gif)](docs/media/full-app-walkthrough-dark.gif)

- **변경 기록 검색** 현재 이름이나 이전 이름, 경로, 파일 참조 번호로 대상을 찾습니다. 기간이나 변경 사유로 검색 결과를 좁힐 수 있습니다.
- **파일별 변경 이력** 검색 결과에서 파일 이력을 엽니다. 이름이나 경로가 바뀌면 변경 전후의 값과 근거 기록을 함께 확인할 수 있습니다. 수집한 MFT로 경로를 보완한 경우에는 그 출처도 표시합니다.
- **트랜잭션 변경 전후 비교** 트랜잭션 로그에서 LSN으로 기록을 찾습니다. Undo와 Redo 양쪽에 값이 남아 있으면 파일 타임스탬프를 비교할 수 있습니다.
- **조사 후보와 근거 확인** 반복해서 이름이 바뀐 뒤 삭제된 파일, SDelete 관련 기록, 타임스탬프 변경 같은 단서를 확인합니다. 후보마다 연결된 근거 기록을 열 수 있습니다.

각 기록의 상세 필드, 원본 바이트(Hex), JSON을 볼 수 있습니다. 결과를 CSV로 저장하거나, 입력 파일에서 남아 있는 USN·트랜잭션 기록을 찾을 수도 있습니다.

## 어떤 자료를 열 수 있나요?

### 디스크 및 증거 이미지

| 형식 | 확장자 | 지원 |
| --- | --- | :---: |
| RAW | `.raw`, `.img`, `.dd`, `.000` / `.001` | ✓ |
| 물리 EWF | `.e01`, `.ex01` | ✓ |
| 논리 EWF | `.l01`, `.lx01` | ✓ |
| QCOW / QCOW2 | `.qcow`, `.qcow2`, `.qcow3` | ✓ |
| VHD / VHDX | `.vhd`, `.vhdx` | ✓ |
| VMDK | `.vmdk` | ✓ |
| AFF1 | `.aff` | ✓ |

물리 이미지에는 읽을 수 있는 NTFS 볼륨이 필요합니다. 분할 이미지는 첫 파일을 선택하고, BitLocker 볼륨은 먼저 잠금을 해제하세요. [이미지 형식별 지원 조건](modules/EvidenceImage/README.md)

### 추출한 자료와 기존 결과

| 자료 | 지원 |
| --- | :---: |
| `$J` / `$UsnJrnl:$J` | ✓ |
| `$MFT` | ✓ |
| `$LogFile` | ✓ |
| NTFS Log Tracker `.db`, `.sqlite`, `.sqlite3`, `.csv`, `.tsv` | ✓ |

경로를 보완하려면 같은 볼륨에서 추출한 `$MFT`를 `$J` 또는 `$LogFile`과 함께 여세요. TSV는 파일 선택 창에서 **모든 파일**을 선택하면 열 수 있습니다.

앱은 원본을 읽기 전용으로 열고 추출 파일과 SQLite 색인을 별도 경로에 저장합니다. 기본 저장 위치는 `%LOCALAPPDATA%\NtfsTracer`입니다. 기존 JournalDesk에서 사용한 설정과 색인도 이어서 사용할 수 있습니다.

## 조사 예시와 도구 비교

### 현재 파일명으로 이전 이름 찾기

수집한 자료에서 `report-final.docx`를 발견했습니다. 이 파일이 예전에 어떤 이름으로 저장됐는지 알아보기 위해 같은 USN 저널(`$J`)을 두 도구에서 열었습니다. NTFS는 이전 이름과 새 이름을 별도 기록에 남기므로, 현재 이름으로 찾은 기록에서 같은 파일의 이전 기록까지 따라가야 합니다.

| 도구 | 이전 이름을 찾은 방법 |
| --- | --- |
| <img src="docs/media/logo.svg" width="18" height="18" alt=""> **NTFS Tracer** | **이전 이름 포함**을 켜고 `report-final.docx`를 검색한 뒤 **파일 이력 보기**를 선택했습니다. 검색에서 같은 파일의 기록 5건을 찾았고, 파일 이력에서는 이름 변경 한 건에 이전 이름과 새 이름이 함께 표시됐습니다. |
| <img src="docs/media/ntfs-log-tracker-icon.png" width="18" height="18" alt=""> **[NTFS Log Tracker 1.9](https://sites.google.com/site/forensicnote/ntfs-log-tracker)** | **File/Directory Name**으로 `report-final.docx`를 검색해 기록 2건을 찾았습니다. 결과에서 NTFS가 파일을 식별하는 **FileReferenceNumber**를 복사하고 이름 조건을 지운 뒤, 그 번호로 다시 검색해 이전 이름이 포함된 기록 5건을 찾았습니다. |

두 도구에서 이전 이름이 **`report-draft.docx`**였음을 확인했습니다.

[![NTFS Tracer 파일 이력에 report-draft.docx에서 report-final.docx로 이름이 변경된 내역과 두 USN 기록이 표시된 화면](docs/media/file-history-dark.png)](docs/media/file-history-dark.png)

**이전 이름을 찾기까지 걸린 시간**

22 GiB RAW 디스크 이미지에서 추출한 USN 저널(92.34 MiB, 378,975건)을 사용했습니다. 표에는 같은 Windows PC에서 도구별로 6회 측정한 시간의 중앙값을 표시했습니다. 두 번째 조회에서는 저널을 열어 둔 상태에서 다른 파일의 이전 이름을 찾았습니다.

| 측정 구간 | NTFS Tracer 0.8.32 | NTFS Log Tracker 1.9 |
| --- | ---: | ---: |
| 분석 시작부터 첫 USN 목록 표시까지 | **1.95초** | 19.57초 |
| 분석 시작부터 첫 번째 파일의 이전 이름 확인까지 | **2.24초** | 20.01초 |
| 저널을 열어 둔 상태에서 두 번째 파일의 이전 이름 확인 | **216 ms** | 356 ms |

두 번째 조회에서는 6회 모두 NTFS Tracer가 먼저 결과를 표시했습니다. 두 도구에서 이전 이름과 새 이름, USN, 변경 사유가 일치했고, 시각도 Tracker가 표시하는 정밀도까지 일치했습니다. [실험 조건과 6회 측정값](docs/performance.md#previous-name-lookup-on-a-378975-record-journal) · [MFTECmd로 위 예제의 원본을 대조한 방법](docs/performance.md#cross-checking-the-demo-with-mftecmd)

## 소스에서 빌드하기

Windows x64 환경에 .NET 10 SDK, Rust MSVC 도구 체인, Visual Studio C++ Build Tools를 설치하세요. 코드 검사를 실행하려면 Rust의 `rustfmt`와 `clippy`도 필요합니다.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

EWF 이미지를 열 수 있는 배포본을 만들려면 `NtfsTracer.libewf.dll`을 빌드한 뒤 `modules/EvidenceImage/Logical/Native/`에 두고 다시 게시하세요. [libewf 빌드 방법](modules/EvidenceImage/Logical/Native/README.md)

## 표시 언어

**보기 → 표시 언어**에서 16개 언어 중 하나를 선택할 수 있습니다. 선택한 언어는 즉시 적용됩니다.

English · 한국어 · 日本語 · Deutsch · Español · Français · Bahasa Indonesia · Italiano · Polski · Português (Brasil) · Türkçe · Tiếng Việt · Русский · Українська · 简体中文 · 繁體中文

## 라이선스

프로젝트 자체 코드는 [Apache License 2.0](LICENSE)에 따라 공개합니다.

외부 구성 요소에는 각 구성 요소의 라이선스가 적용됩니다. [외부 구성 요소와 라이선스](THIRD-PARTY-NOTICES.md)
