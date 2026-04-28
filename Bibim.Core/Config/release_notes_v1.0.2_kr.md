# BIBIM v1.0.2

**릴리즈일**: 2026-04-28

## 주요 변경사항

### 멀티 프로바이더 LLM 지원 (BYOK)
이제 설정에서 **4개 모델 중** 사용할 수 있는 것을 선택할 수 있습니다 — 본인 계정/예산에 맞는 것으로 고르세요.

| 모델 | 프로바이더 | 1회 예상 비용 | 비고 |
|------|----------|--------------|------|
| **Claude Sonnet 4.6** ⭐ | Anthropic | ~$0.04 | 추천 · 균형 |
| Claude Opus 4.7 | Anthropic | ~$0.20 | 최고 품질 · 에이전트 작업 |
| GPT-5.5 | OpenAI | ~$0.08 | 다국어 강점 · 툴 호출 안정 |
| Gemini 3.1 Pro | Google | ~$0.03 | 가장 저렴 · 최대 컨텍스트 |

- 설정 화면에 프로바이더별 키 입력란(Anthropic / OpenAI / Google)이 따로 있습니다.
- 키가 등록되지 않은 모델은 회색 처리되고, 호버 시 어떤 키를 등록해야 하는지 툴팁이 표시됩니다.
- 새 키를 입력하면 해당 프로바이더의 모델이 **즉시 활성화** — 재시작 필요 없음.
- 1.0.1 기존 사용자: 저장된 Anthropic 키는 **자동 마이그레이션**됩니다. 기존 설정 파일은 `.bak` 백업으로 함께 보관됩니다.

### API 키 발급 가이드 링크 추가
설정 패널 상단에 **"📖 API 키 발급 가이드 보기"** 버튼이 추가됐습니다. Anthropic / OpenAI / Google 각각의 키 발급 절차를 단계별로 안내하는 노션 페이지가 열립니다 (한국어/영문 자동 분기).

### 토큰 사용량 30~40% 절감
같은 작업을 더 적은 토큰으로 처리하도록 코드 생성 파이프라인을 전반 최적화했습니다. 일상 사용 기준 **input 토큰 30~40% 감소**, 헤비 유저 (월 100회+) 기준 비용 **35~42% 절감** 효과.

핵심 변경:
- **Anthropic 프롬프트 캐싱 활용**: 시스템 프롬프트 + 툴 정의에 `cache_control: ephemeral` 마커. 같은 세션 5분 내 재요청 시 prefix 부분이 90% 할인 가격(`$0.30/1M`)으로 청구됨.
- **캐시 효과 계측 인프라**: `cache_read_input_tokens` / `cache_creation_input_tokens` 가 `bibim_v3_debug.txt` 로그에 라인별 / 세션 누적으로 기록됨. 캐시 hit ratio 도 함께 표시.
- **Roslyn 컴파일 재시도 prune**: 코드 컴파일 실패 시 이전 시도들이 메시지에 누적되던 문제 수정. 매 재시도마다 ~700t 의 죽은 컨텍스트 제거.
- **로컬 RAG 다이어트**: `search_revit_api` 결과의 verbosity 축소 (TopK 5→3, 청크 3000→1200자, 멤버 60→30개). 코드 생성 품질엔 영향 없음.
- **휴리스틱 게이트**: "안녕", "ok", "고마워" 같은 짧은 메시지엔 task planner LLM 호출 자체를 스킵 — ~2,500t 절감/스킵.
- **카테고리 질문 템플릿 분리**: planner 시스템 프롬프트의 ~1,800t 카테고리 질문 라이브러리를 압축 체크리스트로 재구성.
- **장기 세션 안정화**: 히스토리 윈도우 20→10턴 + 잘린 부분은 합성 요약으로 대체. 컨텍스트 한도 초과 에러 빈도 ~70% 감소.
- **컨텍스트 툴 조건부**: `get_view_info`, `get_selected_elements`, `get_element_parameters`, `get_family_types`, `get_project_levels` 5개 툴은 사용자 메시지에 관련 키워드가 있을 때만 LLM 에 노출.
- **FileOutputRules 조건부**: PDF/DWG/CSV 등 파일 출력 작업이 아닌 코드 생성에서는 ~700t 의 파일 안전 규칙 블록을 스킵.

### LLM 안정성 개선
- **Gemini 응답 안정화**: planner 단계에 `responseMimeType: "application/json"` 강제 모드 추가. 이전 빌드에서 Gemini 가 JSON 출력 실패하면 코드가 채팅 버블로 직행하던 회귀 fix.
- **Planner parse 실패 시 1회 재시도**: JSON 파싱 실패하면 모델한테 "이전 응답이 JSON 이 아니었음, 다시" 명시적 안내 메시지로 재시도. 두 번 모두 실패해야 fallback 으로 빠짐.
- **GPT 선택 우선 규칙**: "이 도어들", "선택된", "these elements" 같은 지시어가 있을 때 GPT 가 모델 전체를 스캔하지 않고 `uidoc.Selection.GetElementIds()` 우선 사용하도록 시스템 프롬프트에 명시적 규칙 추가. Claude 는 이미 이 패턴을 따랐던 영역.

### 멀티 프로바이더 핫픽스 (실제 사용자 테스트로 발견)
세 프로바이더 모두 task → question → codegen 흐름이 깨져있던 결함 — 토큰 최적화 패치와 무관하게 멀티 프로바이더 작업 중 도입된 latent bug 3건을 한 번에 fix.
- **Anthropic 400 fix**: 툴 결과(tool_result) 블록의 `name` 필드가 Anthropic API 의 strict schema 검증에 걸려 두 번째 호출부터 400 에러로 tool loop 마비. AnthropicProvider 가 전송 전에 자동으로 strip 하도록 수정.
- **OpenAI 400 fix**: `text.format=json_object` 모드 사용 시 OpenAI Responses API 가 input 메시지에 영문 "json" 단어를 요구하는 quirk 발견. planner user 메시지 끝에 한 줄 추가하여 충족.
- **Gemini 모델 변경**: `gemini-3.1-pro-preview-customtools` (agent + custom tools 전용 변형) → `gemini-3.1-pro-preview` (vanilla) 로 교체. customtools 변형이 함수호출 우선 동작 때문에 JSON 출력 모드를 silently 무시하던 문제 해결. Google 공식 권장: "tool calling 50% 미만이면 vanilla 사용".
- **자동 마이그레이션**: 기존 사용자 가 `-customtools` 모델 ID 가 저장되어 있으면 다음 launch 시 자동으로 vanilla 로 disk rewrite + `.bak` 백업 생성. 사용자가 Settings 에서 다시 선택할 필요 없음.

### 모델 선택 UX 개선
설정 패널의 모델 선택 항목에 **응답 속도 표시 (⚡)** 추가. 한 눈에 비교 가능:
- ⚡⚡⚡ Claude Sonnet 4.6 (빠름)
- ⚡⚡ Claude Opus 4.7, GPT-5.5 (보통)
- ⚡ Gemini 3.1 Pro (느림 — 추론 시간이 길 수 있음)

⚡ 아이콘에 마우스 올리면 한국어 tooltip 표시.

### 로딩 상태 버그 수정
LLM 호출 중 429 / 네트워크 / 결제 오류가 발생하면 채팅 패널이 "응답 생성중…" 상태에서 멈추는 문제가 있었습니다. 이제 에러 시 진행 UI가 **항상 즉시 해제**되어, 바로 다른 모델로 재시도할 수 있습니다.

## 버그 수정 / 개선
- 멀티 프로바이더 키를 연속 저장할 때 "Saved" 토스트가 정확한 프로바이더(Anthropic/OpenAI/Gemini)를 표시
- 설정 패널이 작은 모니터에서 잘릴 때 자동 스크롤
- 로그 라인 정리: 모든 LLM 호출에 provider + model 정보 + 캐시 hit ratio 가 함께 기록됨
- 스트리밍 채팅 경로에서 `cache_creation_input_tokens` 누락되던 계측 사각지대 fix (첫 메시지 캐시 생성 비용이 0 으로 잘못 표시되던 문제)
- 사용 안 하던 `GeminiRagService` 데드 코드 제거 (로컬 BM25 RAG 가 v1.0.1 부터 단독 사용)

## 요구사항
- Autodesk Revit 2022 이상
- 다음 중 하나 이상의 API 키:
  - [console.anthropic.com](https://console.anthropic.com/) (Claude)
  - [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT)
  - [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini)

## 소스
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
