#!/usr/bin/env bash
# InvenAdClicker 빌드 스크립트 (Linux/dev)
# 사용법: ./build.sh
set -euo pipefail
cd "$(dirname "$0")"

echo "== InvenAdClicker release 빌드 =="
cargo build --release --locked

EXE="./target/release/InvenAdClicker"
if [[ -f "$EXE" ]]; then
    if [[ ! -e "$(dirname "$EXE")/config.json" ]]; then
        cp -n config.json.example "$(dirname "$EXE")/config.json"
    fi
    echo "빌드 완료: $EXE"
    echo "실행: $EXE  (첫 실행 시 인벤 아이디와 비밀번호를 입력)"
else
    echo "실행 파일을 찾지 못했습니다: $EXE" >&2
    exit 1
fi
