# EvidenceImage

EvidenceImage는 JournalDesk가 디스크 이미지와 논리 증거 파일을 읽고 NTFS 아티팩트를 추출할 때 사용하는 .NET 모듈입니다. 모듈 소스와 빌드에 필요한 고지는 이 저장소에 포함되어 있습니다.

물리 디스크 이미지에서는 NTFS 볼륨을 선택하고, 논리 EWF에서는 수집 목록에서 추출할 파일을 선택합니다. 추출한 파일과 매니페스트는 사용자가 지정한 출력 폴더에 저장합니다.

| 입력 | 지원 범위 |
| --- | --- |
| RAW | 단일 NTFS 볼륨, MBR·GPT 디스크, 연속된 세 자리 숫자 확장자로 나뉜 RAW |
| EWF | E01·Ex01 물리 컨테이너, L01·Lx01 논리 컨테이너. 첫 세그먼트를 선택합니다. 암호화, bzip2, 누락된 세그먼트, 수집 불량이 기록된 이미지는 제외됩니다. |
| QCOW·QCOW2 | 독립형 QCOW1과 표준 L2를 사용하는 QCOW2 버전 2·3의 현재 디스크 내용. 외부 backing 파일, 암호화, 외부 데이터와 확장 L2는 지원 범위에서 제외됩니다. |
| VHD·VHDX | 고정·동적 VHD와 독립형 VHDX |
| VMDK | 단일 sparse, stream-optimized, 같은 폴더의 FLAT extent를 참조하는 descriptor. 부모 디스크 체인 및 폴더 밖 extent는 제외됩니다. |
| AFF | 단일 AFF1의 무압축·zlib·제로 페이지 |

EWF의 네이티브 라이브러리는 [여기서 빌드](Logical/Native/README.md)할 수 있습니다. 디스크 및 NTFS 판독에 사용하는 [DiscUtils 고지](Notices/DiscUtils.txt)도 포함되어 있습니다.
