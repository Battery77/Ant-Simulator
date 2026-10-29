# PR #2 독립 검증 산출물

이 브랜치는 **검증 산출물 전달 전용**입니다. 애플리케이션 개발 브랜치나 병합할 수정안이 아닙니다.

검사 대상은 `Battery77/Ant-Simulator` PR #2의 `da05632d1c0a3cbbe0acd569f805ba7f6bd34376`입니다. T1은 한 줄 TestHook을 넣은 별도 복사본에서, T2·T3는 대상 원본에서 재현했습니다. 자세한 조건과 한계는 보고서를 확인하세요.

## 받을 파일

- [한국어 독립 검증 보고서](REPORT.ko.md)
- [전체 증거 묶음](pr2-verification-evidence.tar.gz) — 원본 대상 소스, 훅 복사본, 하네스, 로그, TRX, 보고서, 무결성 목록
- [재실행 안내](REPRODUCE.md)
- [세 파일의 SHA-256](SHA256SUMS)

각 파일을 열고 GitHub의 다운로드 버튼을 이용하거나, 이 브랜치 화면에서 **Code → Download ZIP**으로 모두 받으세요. 저장소가 비공개이면 접근 권한이 있는 GitHub 계정으로 로그인해야 합니다.

## 클라우드 Claude Code에서 가져오기

기존 개발 체크아웃과 다른 새 디렉터리에서 아래 명령을 실행합니다. 이 브랜치를 기존 개발 브랜치에 merge하지 마세요.

```bash
git clone --single-branch --branch evidence/pr2-independent-verification-da05632 \
  https://github.com/Battery77/Ant-Simulator.git pr2-verification-downloads
cd pr2-verification-downloads
sha256sum --check SHA256SUMS
tar -xzf pr2-verification-evidence.tar.gz
cd pr2-verification
sha256sum --check MANIFEST.sha256
```

다시 실행하려면 .NET 8 SDK와 Linux의 `timeout` 명령이 필요합니다. `dotnet`을 PATH에 준비한 뒤:

```bash
bash run.sh
```

재현 하네스는 **기존 결함의 관찰값**을 검증합니다. 종료 코드 0을 제품의 결함 없음 또는 수정 완료로 해석하지 마세요. 로그를 보존하려면 재실행 전에 복사하세요.

## 기록의 시점

세 산출물은 독립 검증 종료 시점의 파일을 바이트 그대로 보존했습니다. 보고서 F절의 “커밋·Push 없음”은 그 검증 종료 시점의 상태입니다. 이후 사용자의 GitHub 전달 요청에 따라 산출물 전용 브랜치에 업로드한 것입니다. 이번 전달로 PR #2, PR #3, main의 코드나 참조를 변경하지 않으며, Pull Request도 생성하지 않습니다.

문서 속 `/workspace/...` 경로는 원래 검증 환경의 경로입니다. 다른 환경에서는 압축 해제한 `pr2-verification/` 폴더 안의 같은 상대 경로를 사용하세요. 원래 환경 설정·게시 작업과 이 산출물 전달은 별개입니다.
