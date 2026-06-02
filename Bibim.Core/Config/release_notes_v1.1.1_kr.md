# BIBIM v1.1.1

**릴리즈일**: 2026-06-03

> **핫픽스 릴리즈** — v1.1.0 출시 후 발견된 3개 결함 정리. 신규 기능 없음, 안정성 회복만.

---

## 한 줄 요약

**Revit 2027 사용자의 코드 생성을 완전히 막던 CS1704 에러 + 모델링 요청이 분석으로 빠지던 라우팅 버그 + 새 버전 알림 바 시각 폭주 — 세 가지 모두 수정.**

---

## 버그 수정

### 1. Revit 2027 — 코드 생성 차단 해제

- **증상**: Revit 2027에서 BIBIM 사용 시 모든 코드 생성에서 `CS1704: An assembly with the same simple name 'Autodesk.Http' has already been imported` 컴파일 에러.
- **원인**: Revit 2027이 `Autodesk.Http.dll`을 두 경로 (메인 설치 + `AddIns\ExtendedAPIs\`)에서 동시 로드. `RoslynCompilerService`가 파일 경로 기준으로 dedupe해서 두 인스턴스 모두 등록 → Roslyn이 거부. 2024~2026엔 `ExtendedAPIs` 폴더 자체가 없어 영향 X.
- **수정**: `BuildBaseReferences()`가 simple assembly name 기준 dedupe, ExtendedAPIs 경로 후순위.
- **영향 받는 사용자**: **Revit 2027 사용자 100%**.

### 2. 모델링 요청 false-positive 라우팅

- **증상**: "벽 만들어줘" / "캐드 인식해서 모델링" 같은 명령이 *모델 요약* 분기로 빠져 코드 생성 안 됨. 사용자가 "ㅇㅇ" / "이제 진행해" 같은 두 번째 메시지로 재촉해야 동작.
- **원인**: `IsBuiltInCurrentContextSummaryTaskV2()`가 "현재 뷰" + "분석" 두 키워드 동시 매치 시 무조건 true 반환. WRITE 동사 (만들/생성/모델링 등) 부재 검사 없음. Q&A 답변이나 planner-생성 단계 텍스트에 트리거 키워드가 우연히 들어가면 오인.
- **수정**: WRITE 동사 negative gate 추가 (만들 / 생성 / 배치 / 추가 / 모델링 / 올려 / 수정 / 변경 / 삭제 / 이동 / 복사 / 회전 / 내보내 / export / create / place / add / build / edit / delete / move / copy / rotate 등 30개).
- **영향**: 첫 시도부터 정상 코드 생성.

### 3. 새 버전 알림 바 시각 폭주

- **증상**: 새 버전 알림 바에 릴리즈 노트 raw markdown 전체가 한 `<span>`에 박혀 ~2,000px 보라색 텍스트 벽 + 버튼 깨짐.
- **원인**: `VersionChecker`가 GitHub release body 전체 (~9KB)를 그대로 `ReleaseNotes`로 전달. `App.tsx`가 길이 제한·CSS clamp 없이 단일 라인 span에 렌더.
- **수정**:
  - 백엔드: `ExtractReleaseHeadline()` 추가 — 첫 의미 있는 한 줄만 추출, 140자 cap. `ReleaseNotesUrl` 별도 필드로 GitHub release 페이지 URL 전달.
  - 프론트: alert 바 `maxHeight: 44` + 단일 라인 ellipsis. 우측에 **"전체 보기 ↗"** 링크 추가 — 클릭 시 GitHub release 페이지가 외부 브라우저로 열림.
- **영향**: 모든 사용자.

---

## 자동 마이그레이션

해당 없음 — 데이터 / 설정 변경 없음. 업데이트 후 즉시 정상 동작.

---

## 빌드 / 배포

| 빌드 타겟 | 결과 |
|----------|------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

## 소스

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
