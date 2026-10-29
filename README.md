# Ant-Simulator
개미 페로몬 시뮬레이터 — 게임형 + 과학 실험형 2D 개미 생태계 시뮬레이터 (Godot 4 .NET / C#)

## 구성
- `src/AntSim.Core` — Godot에 의존하지 않는 시뮬레이션 코어 (월드, 편집기, 냄새, 시뮬레이션, 저장 형식)
- `godot/` — Godot 4.4+ .NET 프론트엔드 (편집 모드 / 시뮬레이션 모드)
- `tests/AntSim.Core.Tests` — 단위 테스트 (Godot 없이 실행)
- `tools/AntSim.Benchmarks` — 기준 성능 측정
- `tools/godot-smoke-test.sh` — Godot 통합 점검

## 빌드 / 테스트
.NET 8 SDK 필요. Godot 프로젝트도 NuGet의 Godot.NET.Sdk로 `dotnet build`가 된다.
```
dotnet build
dotnet test
dotnet run -c Release --project tools/AntSim.Benchmarks
GODOT=/path/to/Godot_mono tools/godot-smoke-test.sh            # 헤드리스
GODOT=/path/to/Godot_mono tools/godot-smoke-test.sh --display  # xvfb: 렌더링·마우스 입력·스크린샷
```

## 실행
Godot 4.4 이상 .NET 버전으로 `godot/project.godot`를 연다. 화면 위쪽에서 편집 모드 / 시뮬레이션 모드를 전환한다.
시뮬레이션은 편집 월드의 복제본에서 실행되며 원본 맵에 반영되지 않는다. 맵은 `.antmap`, 실험 상태는 `.antexp`로 따로 저장한다.

편집 모드
| 조작 | 동작 |
|---|---|
| 왼쪽 클릭·드래그 | 선택한 도구로 편집 (드래그 한 번 = 실행 취소 1단계) |
| 가운데/오른쪽 드래그, 휠 | 화면 이동, 확대/축소 |
| Tab | 지상 ↔ 개미집 전환 |
| Ctrl+Z / Ctrl+Y (Ctrl+Shift+Z) | 실행 취소 / 다시 실행 |
| Ctrl+S | 저장 |
| 조회 도구로 클릭 / Esc | 타일 정보 고정 / 해제 |

시뮬레이션 모드
| 조작 | 동작 |
|---|---|
| Space / . | 재생·일시정지 / 단일 틱 |
| 패널 | 초기화, 속도, 히트맵(끄기 / 음식 / 유충 / 복합: 음식 + 유충, 선형/로그, 냄새별 자동·고정 최대값과 범례), 냄새 설정(틱·D·Dp·k·e, 검증 후 적용), 실험 저장/불러오기 |
| 클릭 / Tab | 타일 정보·냄새 농도 고정 / 레이어 전환 |

## 문서
- [아키텍처](docs/ARCHITECTURE.md)
- [작업 계획·테스트·성능 측정 계획](docs/PLAN.md)
- [결정이 필요한 규칙](docs/OPEN_QUESTIONS.md)
- [기준 성능 측정](docs/BENCHMARKS.md)
