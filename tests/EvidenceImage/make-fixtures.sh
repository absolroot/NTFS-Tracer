#!/usr/bin/env bash
set -euo pipefail
script="$(cd "$(dirname "$0")" && pwd)/fixture_data.py"
if [[ $# != 1 || -e "$1" ]]; then
    echo 'Usage: bash make-fixtures.sh <new output directory>' >&2
    exit 2
fi
for tool in python3 mkntfs ntfscp fls icat qemu-img ewfacquirestream affconvert; do command -v "$tool" >/dev/null; done
mkdir -p "$1"
cd "$1"
base="$PWD"
mkdir Alpha Beta split splitmissing missing
for label in Alpha Beta; do
    cd "$base/$label"
    truncate -s 24M volume.raw
    mkntfs -Q -F -s 512 -c 4096 -L "QA-$label" volume.raw >format.log 2>&1
    printf '%s NTFS fixture\n' "$label" > sample.txt
    ntfscp -f volume.raw sample.txt '/$Extend/$UsnJrnl'
    ntfscp -f volume.raw sample.txt "/$label-renamed.txt"
    if [[ "$label" == Alpha ]]; then
        python3 "$script" padding "$PWD"
        ntfscp -f volume.raw ordinary.bin /ordinary.bin
    fi
    file_number="$(fls -f ntfs volume.raw | sed -nr "s@^r/r ([0-9]+)-[0-9]+-[0-9]+:.*$label-renamed.txt@\\1@p")"
    icat -f ntfs volume.raw 0-128-1 > mft-before.bin
    python3 "$script" metadata "$PWD" "$label" "$file_number"
    ntfscp -f -N '$J' volume.raw journal.bin '/$Extend/$UsnJrnl'
    ntfscp -f -i volume.raw log.bin 2
    stream="$(fls -r -f ntfs volume.raw | awk '$NF=="$UsnJrnl:$J"{sub(/:$/, "", $3); print $3}')"
    icat -f ntfs volume.raw "$stream" > expected-j.bin
    icat -f ntfs volume.raw 0-128-1 > tsk-mft.bin
    icat -f ntfs volume.raw 2-128-1 > expected-l.bin
    icat -f ntfs volume.raw 6-128-1 > tsk-bitmap.bin
    cmp journal.bin expected-j.bin
    cmp log.bin expected-l.bin
done
cd "$base"
python3 "$script" disks "$PWD"
qemu-img convert -f raw -O vpc -o force_size=on,subformat=fixed case-mbr.raw case-fixed.vhd
qemu-img convert -f raw -O vpc -o force_size=on,subformat=dynamic case-mbr.raw case-dynamic.vhd
qemu-img convert -f raw -O vhdx case-mbr.raw case.vhdx
qemu-img convert -f raw -O vmdk -o subformat=monolithicSparse case-mbr.raw case.vmdk
qemu-img convert -f raw -O vmdk -o subformat=streamOptimized case-mbr.raw case-stream.vmdk
qemu-img convert -f raw -O vmdk -o subformat=monolithicFlat case-mbr.raw case-flat.vmdk
qemu-img convert -f raw -O qcow case-mbr.raw case-v1.qcow
qemu-img convert -f raw -O qcow2 -o compat=0.10 case-mbr.raw case-v2.qcow2
qemu-img convert -f raw -O qcow2 -o compat=1.1 case-mbr.raw case-v3.qcow2
qemu-img convert -f raw -O qcow2 -c case-mbr.raw case-compressed.qcow2
qemu-img create -f qcow2 -b case-mbr.raw -F raw case-backing.qcow2
head -c 18000 case.vmdk > truncated.vmdk
ewfacquirestream -q -c fast -t case -S 1048576 -C SYNTHETIC-NTFS -D synthetic-only < case-mbr.raw > ewf.log
cp case.E01 case.E03 missing/
affconvert -o case.aff case-mbr.raw > aff.log 2>&1
echo "Fixtures ready in $PWD"

