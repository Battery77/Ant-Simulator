#!/usr/bin/env bash
# Godot 프론트엔드 통합 점검. 사용법: GODOT=/path/to/Godot_mono tools/godot-smoke-test.sh [--display]
#   기본: 헤드리스 실행 (편집·렌더 갱신·Undo/Redo·저장/불러오기)
#   --display: xvfb-run으로 실제 렌더링 + 마우스 입력 점검 + 스크린샷(godot-smoke.png)
set -euo pipefail
GODOT="${GODOT:?GODOT 환경 변수에 Godot .NET 실행 파일 경로를 지정하세요}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

dotnet build "$ROOT/godot/AntSim.App.csproj" -nologo -v q
if [[ "${1:-}" == "--display" ]]; then
  xvfb-run -a -s "-screen 0 1600x900x24" "$GODOT" --path "$ROOT/godot" --rendering-driver opengl3 \
    -- --smoke-test --screenshot="$ROOT/godot-smoke.png"
else
  "$GODOT" --headless --path "$ROOT/godot" -- --smoke-test
fi
