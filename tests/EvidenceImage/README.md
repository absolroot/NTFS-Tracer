# 이미지 추출 회귀 검사

같은 합성 NTFS 자료를 여러 컨테이너로 변환한 뒤, Windows x64에서 공개 API로 추출한 파일과 독립 도구의 출력을 대조한다. 검증 자료는 사용자가 지정한 빈 경로에 생성한다. 이미 존재하는 경로를 전달하면 생성기가 중단된다.

## 기본 자료 생성

Linux 또는 WSL에서 Python 3.9 이상, ntfs-3g, Sleuth Kit, qemu-img, ewf-tools, afflib-tools가 필요하다. Ubuntu 24.04에서 검증했다. 아래 명령은 NTFS Tracer 저장소 루트에서 실행한다.

```bash
bash tests/EvidenceImage/make-fixtures.sh /mnt/c/Temp/ntfs-tracer-synthetic
```

다음 명령은 Windows PowerShell에서 실행한다. .NET 10 SDK가 필요하다.

```powershell
dotnet run -c Release --project tests/EvidenceImage -- C:\Temp\ntfs-tracer-synthetic
```

- Alpha와 Beta에는 이름 변경 전후와 닫기 기록을 담은 USN Journal, 해당 파일의 MFT 항목, 트랜잭션 두 건을 담은 로그가 있다. `Alpha/volume.raw`는 NTFS Tracer의 GUI 검수에도 사용할 수 있다.
- $MFT는 TSK `icat`으로 추출한 바이트를 기준으로 삼는다. 레코드의 USA를 복원하는 도구를 거치면 섹터 끝의 바이트가 원본과 달라진다.
- 기본 검사는 볼륨 단독 RAW, MBR, GPT의 두 볼륨, 분할 RAW, VHD 고정·동적, VHDX, VMDK의 sparse·streamOptimized·descriptor와 flat extent, QCOW1·2·3과 zlib 압축, AFF1, 3개 세그먼트의 E01을 다룬다.
- 각 볼륨에서 파일 추출과 manifest의 크기·해시를 확인한다. 출처로 기록한 컨테이너와 세그먼트의 해시도 실제 파일과 대조한다. 일반 추출 API는 같은 볼륨에서 `$Bitmap`을 독립적으로 읽는다.
- 압축 QCOW2와 분할 RAW는 `EvidenceDisk.OpenRead`로 열고 섹터 안쪽, 세그먼트 경계, 디스크 끝을 비순차적으로 읽어 원시 디스크와 대조한다. 쓰기가 거부되는지도 확인한다.
- 분할 중간 파일 누락, 잘린 이미지, 잘못된 GPT CRC, backing file이 필요한 QCOW, 잘못된 볼륨 ID, 요청한 두 파일 중 하나가 없는 경우, 시작 전·원본 해시 계산 중 취소, 동시 실행을 확인한다.

## Ex01·L01·Lx01 추가 검사

별도로 만든 수집기가 쓴 컨테이너를 검증하려면 Rust와 Cargo가 필요하다. 아래 Bash 명령은 이 작업공간 루트에서 실행한다. 이 예제는 검증용 소스와 도구를 임시 디렉터리에 둔다. 테스트하는 커밋의 잠금 파일을 사용한다.

```bash
test_tools="$(mktemp -d)"
git clone --quiet https://github.com/ebrig/ewf-image "$test_tools/ewf-image"
git -C "$test_tools/ewf-image" checkout --quiet 04137ee9729478ba37713d212806d360ddc5ef8b
cp tests/EvidenceImage/ewf_fixtures.rs "$test_tools/ewf-image/examples/ewf_fixtures.rs"
cargo run --locked --manifest-path "$test_tools/ewf-image/Cargo.toml" --example ewf_fixtures -- /mnt/c/Temp/ntfs-tracer-synthetic
```

```powershell
dotnet run -c Release --project tests/EvidenceImage -- C:\Temp\ntfs-tracer-synthetic --extended-ewf
```

Ex01에는 MBR 디스크 전체가 들어 있다. L01과 Lx01에는 카탈로그의 `Volume` 폴더 아래에 수집된 파일이 들어 있다. 논리 이미지에서도 NTFS Tracer용 기본 파일을 추출하는 API와 카탈로그의 정확한 경로를 지정하는 일반 API를 각각 확인한다. 디스크의 섹터를 읽는 API에서 L01을 거부하는지도 점검한다.

