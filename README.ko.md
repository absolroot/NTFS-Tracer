# NTFS Tracer

NTFS Tracer는 Windows에서 NTFS 파일의 변경 이력을 조사하는 도구입니다. 파일명이나 경로로 기록을 찾은 뒤, 파일별 이력에서 생성·수정·이름 변경·이동·삭제 기록을 시간순으로 볼 수 있습니다. 이름이 바뀐 경우에는 이전 이름과 새 이름을 함께 표시하고, 각각의 근거가 된 USN 기록을 연결합니다.

[Windows x64 다운로드](https://github.com/absolroot/NTFS-Tracer/releases/latest) · [English](README.md) · [日本語](README.ja.md)

[![파일명을 검색하고 파일 이력에서 이름 변경 전후를 확인하는 영어 데모](docs/media/investigation.gif)](docs/media/investigation.gif)

데모는 파일 131개의 변경 기록 391건이 담긴 합성 저널을 사용합니다. `report`를 검색하고 파일 이력을 열면, `report-draft.docx`가 `report-final.docx`로 바뀐 기록을 확인할 수 있습니다.

## 시작하기

배포 ZIP의 압축을 푼 뒤 `NtfsTracer.exe`를 실행하세요. 경량판을 실행하려면 .NET 10 Windows Desktop Runtime(x64)이 필요합니다.

1. 처음 실행하면 **설정** 창이 열립니다. 준비한 자료에 따라 다음 방법으로 분석을 시작하세요.
   - **이미 추출한 자료가 있으면** `$J`, `$MFT`, `$LogFile` 또는 이 파일들이 들어 있는 폴더를 선택하고 **분석 시작**을 누르세요. 개별 파일이나 폴더를 창에 끌어 놓을 수도 있습니다.
   - **디스크 이미지가 있으면** 이미지 파일을 선택한 뒤, NTFS 볼륨과 추출할 항목을 고르고 **추출 후 분석**을 누르세요.
2. **전체 로그**에서 파일을 검색하세요. 결과에서 파일 이력을 열면 이전 이름과 경로를 확인할 수 있습니다. `$LogFile`을 열었다면 **NTFS 트랜잭션** 탭에서 기록을 조회할 수 있습니다.
3. 기록을 선택하면 개요에 변경 사유, 경로를 판단한 근거, 원본 파일 안에서의 위치가 표시됩니다. `F1`을 누르면 사용 안내가 열립니다.

## 어떤 자료를 열 수 있나요?

| 자료 | 확인할 수 있는 내용 |
| --- | --- |
| `$J` | USN 변경 기록과 파일별 이력을 확인합니다. 파일 이력은 `$J`만 있어도 볼 수 있습니다. |
| `$MFT` | 같은 볼륨에서 추출한 `$J` 또는 `$LogFile`과 함께 열면 파일과 폴더의 경로를 보완할 수 있습니다. |
| `$LogFile` | 트랜잭션 기록, 로그 순서 번호(LSN), Redo·Undo 정보를 확인합니다. |
| NTFS Log Tracker의 SQLite DB·CSV·TSV | **파일** 메뉴에서 기존 결과를 읽기 전용으로 열어 검색할 수 있습니다. TSV 파일을 고를 때는 파일 선택 창에서 **모든 파일**을 선택하세요. |

디스크 이미지에서는 NTFS 볼륨을 선택하면 분석에 필요한 파일을 추출할 수 있습니다. 논리 증거 이미지에서는 이미 수집된 파일 목록에서 필요한 자료를 고릅니다.

| 이미지 형식 | 지원 범위 |
| --- | --- |
| RAW | 단일 NTFS 볼륨, MBR·GPT 디스크, `.000`이나 `.001`부터 번호가 이어지는 분할 이미지 |
| E01·Ex01 | 물리 디스크 이미지. 분할 이미지에서는 첫 파일을 선택하세요. 암호화되었거나 bzip2로 압축된 이미지는 열 수 없습니다. |
| L01·Lx01 | 논리 증거 이미지. 수집된 목록에서 분석할 NTFS 파일을 선택하세요. |
| QCOW·QCOW2 | 독립형 QCOW1과 표준 L2 테이블을 사용하는 QCOW2 v2·v3 |
| VHD·VHDX | 고정·동적 VHD와 독립형 VHDX |
| VMDK | 단일 희소(sparse) 이미지, 스트림 최적화 이미지, 같은 폴더의 FLAT 파일을 참조하는 디스크 설명자 |
| AFF1 | 무압축 페이지, zlib 압축 페이지 또는 0으로 채운 페이지를 담은 단일 이미지 |

잠겨 있는 BitLocker 볼륨은 외부 도구에서 잠금을 해제한 뒤 열 수 있습니다. 이미지 형식별 세부 조건은 [이미지 모듈 안내](https://github.com/absolroot/NTFS-Tracer/blob/v0.8.30/modules/EvidenceImage/README.md)에서 확인할 수 있습니다.

앱은 원본을 읽기 전용으로 열고, 추출한 파일과 SQLite 색인은 별도 경로에 저장합니다. 기본 저장 위치는 `%LOCALAPPDATA%\NtfsTracer`입니다. 기존 JournalDesk에서 사용한 설정과 색인도 이어서 사용할 수 있습니다.

## 조사 기능

- **검색 조건을 좁힙니다.** 파일명, 경로, 파일 ID, 상위 폴더 ID로 검색할 수 있습니다. USN 기록은 기간이나 변경 사유로도 좁힐 수 있습니다.
- **파일의 변경 과정을 읽습니다.** 파일 이력에서 이름과 위치가 어떻게 바뀌었는지 확인합니다. 수집 시점의 `$MFT`로 보완한 경로는 변경 당시의 경로와 다를 수 있습니다. 앱은 경로를 구성할 때 참고한 자료를 표시합니다.
- **개별 기록을 살펴보거나 결과를 저장합니다.** 레코드 상세, Hex, JSON으로 기록을 자세히 확인할 수 있습니다. 검색 결과나 선택한 파일의 이력은 CSV로 내보낼 수 있습니다.
- **남은 기록을 찾습니다.** 입력 파일에서 USN 저널이나 트랜잭션 기록으로 보이는 부분을 찾을 수 있습니다. 조사 후보가 발견되면 연결된 원본 기록을 대조하세요.

## 현재 이름만 알 때 이전 이름 찾기

현재 이름이 `report-final.docx`라는 것만 알고 있다고 가정하고, 두 앱에서 같은 `$J`를 열어 이전 이름을 찾아봤습니다.

| 도구 | 찾아가는 방법 | 표시된 결과 |
| --- | --- | --- |
| **NTFS Tracer** | **이전 이름 포함**이 켜진 상태에서 현재 이름을 검색하고 **파일 이력 보기**를 누릅니다. | 검색 결과에 해당 파일의 기록 5건이 나옵니다. 파일 이력에서 이름 변경 한 건을 선택하면 이전 이름과 새 이름을 나란히 확인할 수 있습니다. |
| **NTFS Log Tracker 1.9** | **File/Directory Name**에 현재 이름을 입력합니다. 결과에서 **FileReferenceNumber**를 확인하고, 이름 조건을 지운 뒤 그 번호로 다시 검색합니다. | 이름으로 검색했을 때는 현재 이름으로 남은 2건이 나옵니다. 같은 파일 참조 번호로 검색하면 이전 이름이 포함된 5건을 볼 수 있습니다. |

두 앱에서 이전 이름이 `report-draft.docx`였음을 확인했습니다. 파일이 다른 폴더로 이동했을 때도 같은 파일 참조 번호로 앞뒤 기록을 따라갈 수 있습니다. 전체 경로를 확인하려면 상위 폴더에 관한 근거가 필요합니다. [영어 화면과 MFTECmd 교차 확인 결과](README.md#compare-workflows)

## 트랜잭션 원본 대조와 조사 재개

| 도구 | 선택한 `$LogFile` 레코드에서 확인할 수 있는 정보 |
| --- | --- |
| **ArtiFast Windows** | 목록에는 원본 오프셋, 레코드 페이지, 현재·이전 LSN, Client Undo LSN, 레코드 유형이 표시됩니다. 행을 선택하면 Transaction ID와 Redo·Undo 작업 등 개별 필드를 볼 수 있습니다. |
| **NTFS Tracer** | **NTFS 트랜잭션**에서 LSN을 검색하고 레코드를 선택하면 원본 위치와 연결된 LSN을 볼 수 있습니다. 페이로드에 파일 속성 시각이 있으면 생성, 내용 수정, 메타데이터 변경, 마지막 접근 시각을 Redo와 Undo별로 나란히 표시합니다. |

ArtiFast의 화면은 제품 문서를 기준으로 정리했습니다. [ArtiFast의 Windows LogFile 문서](https://forensafe.com/blogs/windowslogfile.html)

이전 이름을 확인하기 위해 파일 참조 번호를 기억하고 다시 검색하는 절차를 줄이는 데 초점을 맞췄습니다. 중단된 작업을 다시 시작할 때 기존 작업의 단서가 도움이 된다는 실험 결과도 설계에 참고했습니다. 아래 표는 예제 자료에서 조사 절차에 걸린 시간을 보여 줍니다. [Altmann과 Trafton의 작업 재개 연구](https://interruptions.net/literature/Altmann-CogSci04.pdf)

조사 저장 기능을 지원하는 빌드에서는 **파일 → 조사 저장…**을 선택한 뒤 **마지막 조사 이어서 열기** 또는 **조사 열기…**로 돌아올 수 있습니다. 조사 파일에는 원본 경로와 해시, 작업 탭, 복원 가능한 검색어와 선택 항목이 저장됩니다. 다시 열면 원본의 SHA-256을 확인하고 호환되는 로컬 색인을 재사용합니다.

## Tracker와 비교한 조사 시간

두 앱에 같은 USN 저널과 현재 파일명 두 개를 주고, 각 파일의 이전 이름을 찾는 데 걸린 시간을 쟀습니다. 위 절차대로 Tracker에서는 파일 참조 번호로 다시 검색했고, Tracer에서는 파일 이력을 열었습니다.

| 무엇을 쟀나요? | NTFS Tracer 개발 빌드 | NTFS Log Tracker 1.9 |
| --- | ---: | ---: |
| 분석 시작 버튼을 누른 뒤 새 DB를 만들고 첫 USN 목록을 보기까지 | **1.08초** | 3.25초 |
| 분석 시작 버튼을 누른 뒤 `report-final.docx`의 이전 이름을 확인하기까지 | **1.30초** | 3.50초 |
| 같은 저널에서 `budget-final.docx`의 이전 이름을 확인하기까지 | 127ms | 154ms |

한 PC에서 약 6.1MB의 소규모 합성 저널(63,007건)을 사용했습니다. 각 앱을 한 번씩 실행한 것을 한 쌍으로 묶고, 10쌍의 측정값에서 중앙값을 구했습니다. 첫 파일은 10번 모두 Tracer에서 먼저 확인했습니다. 두 번째 파일은 Tracer가 7번, Tracker가 3번 더 빨랐습니다. 조작은 자동화했으며 파일을 고르거나, 검색어를 입력하거나, 화면을 읽는 데 사람이 쓰는 시간은 제외했습니다. [측정 절차, 결과 대조, 10회 기록](docs/performance.md#finding-a-previous-name-from-the-current-name)

## 소스에서 빌드하기

Windows x64 환경에 .NET 10 SDK, Rust MSVC 도구 체인, Visual Studio C++ Build Tools를 설치하세요. 코드 검사를 실행하려면 Rust의 `rustfmt`와 `clippy`도 필요합니다.

```powershell
cargo build --release --locked --manifest-path engine/Cargo.toml
dotnet publish gui/NtfsTracer.csproj -c Release -p:PublishProfile=Portable -o artifacts/portable
./lint.ps1
```

EWF 이미지를 열 수 있는 배포본을 만들려면 `NtfsTracer.libewf.dll`을 빌드한 뒤 `modules/EvidenceImage/Logical/Native/`에 두고 다시 게시하세요. [libewf 빌드 방법](https://github.com/absolroot/NTFS-Tracer/blob/main/modules/EvidenceImage/Logical/Native/README.md)

소스는 Rust 색인 엔진, WPF 앱, 이미지 판독 모듈로 구성됩니다. [엔진](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/engine) · [WPF 앱](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/gui) · [이미지 모듈](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/modules/EvidenceImage) · [회귀 검사](https://github.com/absolroot/NTFS-Tracer/tree/v0.8.30/tests)

## 표시 언어

**보기 → 표시 언어**에서 16개 언어 중 하나를 선택할 수 있습니다. 선택한 언어는 즉시 적용됩니다.

## 라이선스

프로젝트 자체 코드는 PolyForm Noncommercial 1.0.0에 따라 공개합니다. 영리 기업이 내부 조사에 사용하거나, 고객을 대상으로 유료 조사를 수행하거나, 상용 제품에 포함하려면 사전에 서면 허가를 받아야 합니다. [프로젝트 라이선스](LICENSE)

허가를 요청하려면 GitHub 이슈에 사용 목적과 연락 방법을 적어 주세요. 공개 이슈에는 사건 정보나 증거를 올리지 마세요. [상업적 이용 문의](https://github.com/absolroot/NTFS-Tracer/issues/new)

외부 구성 요소에는 각 구성 요소의 라이선스가 적용됩니다. [외부 구성 요소와 라이선스](THIRD-PARTY-NOTICES.md)
