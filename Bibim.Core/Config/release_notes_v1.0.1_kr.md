# BIBIM v1.0.1

**릴리즈일**: 2026-04-21

## 주요 변경사항

### RAG 재활성화 — 로컬 BM25 검색
RAG(Revit API 문서 검색)가 **돌아왔습니다.** Gemini API 키 없이도 사용할 수 있습니다.

원격 Gemini fileSearch 스토어 대신, BIBIM이 이제 `RevitAPI.xml`에서 직접 BM25 검색 인덱스를 로컬로 빌드합니다. 이 파일은 Autodesk가 모든 Revit 설치본에 함께 제공하는 파일로, 공식 API 문서의 원본 소스와 동일합니다.

- **별도 설정 불필요** — 기존 Claude API 키만으로 즉시 사용 가능
- **첫 사용 후 즉시 응답** — 첫 코드 생성 시 인덱스 1회 빌드(약 0.5초), 이후 세션 내 캐시 재사용
- 전체 Revit 도메인 커버: 핵심 DB, UI, MEP, 구조, IFC (멤버 39,770개, 청크 2,849개)
- Revision Cloud, TextNote, 태그 등 어노테이션 배치 코드 생성 전 타겟 뷰·위치·연결 Revision 여부를 먼저 질문하도록 개선
- Gemini API 키 입력란은 설정 화면에 유지 — 향후 기능 확장용으로 예약

## 버그 수정 / 개선
- 이번 패치에서 별도 버그 수정 없음

## 요구사항
- Autodesk Revit 2022 이상
- Claude API 키 ([console.anthropic.com](https://console.anthropic.com/))

## 소스
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
