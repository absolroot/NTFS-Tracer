# NTFS Tracer

NTFS Tracer는 Windows에서 NTFS 변경 이력과 트랜잭션 로그를 조사하는 데스크톱 도구입니다. 명령줄 파서로 자료를 내보낸 뒤, 결과 표에서 검색하고 원본을 다시 여는 과정에서 분석가는 여러 화면을 오가게 됩니다. NTFS Tracer는 이 작업을 한 화면 흐름으로 연결했습니다. 저널 색인이 준비되면 검색을 시작하고, 파일별 이력에서 이름 변경이나 이동을 추적해 변경 전후 이름과 위치를 비교할 수 있습니다. 사건에 연결된 기록을 확인한 뒤 기존 검색 결과로 돌아올 수 있습니다.

[v0.8.29 영어 데모](docs/media/investigation.gif)에서는 파일 131개가 포함된 합성 저널로 이 조사 과정을 보여 줍니다.

[English](README.md) · [日本語](README.ja.md)

## 시작하기

Windows x64에서 배포 ZIP의 압축을 모두 푼 뒤 `NtfsTracer.exe`를 실행하세요. 경량 배포본에는 .NET 10 Windows Desktop Runtime(x64)이 필요합니다.

1. 자료 없이 실행하면 **설정** 창이 열립니다. **파일 선택**에서 추출한 `$J`, `$MFT`, `$LogFile` 또는 디스크 이미지를 고릅니다. 추출 폴더나 개별 파일을 창으로 끌어 놓을 수도 있습니다. 테마를 바꾸거나 색인·CSV 저장 폴더의 경로를 붙여넣을 수 있습니다.
2. 추출 파일을 열 때는 **분석 시작**을 누릅니다. 디스크 이미지에서는 대상 볼륨과 추출할 항목을 확인한 뒤 **추출 후 분석**을 누릅니다.
3. **전체 로그** 또는 **NTFS 트랜잭션**에서 기록을 선택합니다. 하단 개요에서 선택한 기록의 변경 사유, 경로 근거와 원본 위치를 확인할 수 있습니다. `F1`을 누르면 앱 안에서 사용 안내가 열립니다.

| 조사할 내용 | 필요한 자료 |
| --- | --- |
| NTFS 변경 이력, 이전 파일명, 파일별 사건, 조사 후보 | `$J` |
| 경로 보완 | `$J` 또는 `$LogFile`, 그리고 같은 볼륨에서 추출한 `$MFT` |
| Redo·Undo, LSN, 트랜잭션 원본 | `$LogFile` |

## 조사 기능

- 전체 로그에서 기간과 변경 사유를 지정하고 파일명, 경로, 파일 ID 또는 상위 폴더 ID를 검색합니다. 기록을 선택한 뒤 개요의 **이 ID로 검색**으로 관련 기록을 조회할 수 있습니다.
- 파일 이력에서 이전 이름과 위치를 따라가며 변경 전후를 비교합니다. 레코드 상세, Hex, JSON에서 선택한 기록의 원본 위치와 값을 확인합니다.
- NTFS 트랜잭션에서 LSN과 Redo·Undo를 확인합니다. `$MFT`로 보완한 경로는 수집 당시의 폴더 정보에서 왔으므로 작업 시각의 경로와 다를 수 있습니다. **경로 근거**에서 출처를 구분해 표시합니다.
- `$J`와 `$LogFile`의 흔적 검색, USN 조사 후보, 검색 결과 CSV 저장과 NTFS Log Tracker 결과 열기를 제공합니다. 흔적과 조사 후보는 원본 기록에서 확인할 대상을 좁히는 데 사용합니다.

기존 [NTFS Log Tracker](https://sites.google.com/site/forensicnote/ntfs-log-tracker)의 SQLite DB와 CSV는 읽기 전용 뷰어에서 열 수 있습니다. 파일 이력과 사건에 연결된 기록을 확인하려면 NTFS Tracer에서 추출한 NTFS 원본을 여세요.

## 입력과 저장

물리 디스크 이미지에서 NTFS 볼륨을 찾아 분석에 필요한 파일을 추출합니다. FAT32, exFAT, ReFS 등 다른 파일 시스템은 분석하지 않습니다. 논리 증거 이미지(L01·Lx01)에서는 수집된 파일 목록에서 NTFS 분석에 필요한 파일을 선택할 수 있습니다.

### 디스크 이미지와 논리 증거 이미지

| 형식 | 지원 범위 |
| --- | --- |
| RAW | NTFS 볼륨, MBR·GPT 디스크, `.000` 또는 `.001`부터 시작하는 연속 분할 이미지 |
| E01·Ex01 | 물리 EWF. 첫 세그먼트를 선택하며, 암호화 및 bzip2 압축은 지원 범위에서 제외됩니다. |
| L01·Lx01 | 논리 EWF. 수집 파일 목록에서 NTFS 아티팩트를 고릅니다. |
| QCOW·QCOW2 | 독립형 QCOW1, 표준 L2를 사용하는 QCOW2 v2·v3 |
| VHD·VHDX | 고정·동적 VHD와 독립형 VHDX |
| VMDK | 단일 sparse, stream-optimized, 같은 폴더의 FLAT extent를 참조하는 descriptor |
| AFF1 | 단일 이미지의 무압축·zlib·제로 페이지 |

세부 제약은 [이미지 모듈 안내](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/README.md)를 참고하세요.

원본은 읽기 전용으로 열고, 추출물과 SQLite 색인은 별도 경로에 저장합니다. 새로 설치하면 기본 저장 경로로 `%LOCALAPPDATA%\NtfsTracer`를 사용합니다. 기존 JournalDesk 데이터 폴더가 있으면 해당 경로의 설정과 색인을 이어서 사용할 수 있습니다.

## 소스에서 빌드하기

Windows x64, .NET 10 SDK, Rust의 MSVC 도구 체인, Visual Studio C++ Build Tools가 필요합니다. 코드 검사에는 Rust의 `rustfmt`와 `clippy`도 필요합니다.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

EWF 기능을 사용하려면 [libewf 빌드 안내](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/Logical/Native/README.md)에 따라 `JournalDesk.libewf.dll`을 빌드하세요. 완성한 DLL을 같은 소스 폴더에 두고 다시 게시하면 배포 파일에 포함됩니다.

Rust 엔진은 [`engine/`](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/engine), WPF 앱은 [`gui/`](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/gui), 이미지 모듈은 [`modules/EvidenceImage/`](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/modules/EvidenceImage), 회귀 검사는 [`tests/`](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/tests)에 있습니다. [색인 형식](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/engine/INDEX_FORMAT.md)과 [이미지 테스트 자료 생성 절차](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/tests/EvidenceImage/README.md)도 소스에 포함되어 있습니다.

## 표시 언어

**보기 → 표시 언어**에서 16개 언어를 선택할 수 있습니다. 변경 사항은 즉시 적용됩니다.

## 외부 구성 요소

[Sootmark USN](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/engine/sootmark-LICENSE.txt) · [Sootmark NTFS log](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.29/engine/vendor/sootmark-ntfslog) · [DiscUtils](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/Notices/DiscUtils.txt) · [libewf 및 zlib](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/modules/EvidenceImage/Logical/Native/README.md) · [Fluent System Icons](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.29/gui/Notices/Fluent-System-Icons.txt)



## 라이선스

프로젝트 자체 코드는 [PolyForm Noncommercial 1.0.0](LICENSE)에 따라 공개합니다. 영리 기업의 내부 조사, 유료 고객 조사, 상용 제품에 포함하는 경우에는 사전 서면 허가가 필요합니다. [GitHub 이슈](https://github.com/absolroot/NTFS-Tracer/issues/new)에 이용 목적과 연락 방법을 남겨 주세요. 공개 이슈에는 사건 정보와 증거를 첨부하지 마세요. [외부 구성 요소](THIRD-PARTY-NOTICES.md)에는 각 구성 요소의 라이선스가 적용됩니다.
