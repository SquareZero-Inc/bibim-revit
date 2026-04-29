# BIBIM v1.0.3

**릴리즈일**: 2026-04-29

> **핫픽스 릴리즈** — v1.0.2 출시 후 실제 사용자 테스트에서 드러난 멀티 프로바이더 잠재 결함 7건을 한 번에 정리. 신규 기능 없음, 안정성 회복에만 집중.

---

## 한 줄 요약

**Claude / GPT-5.5 / Gemini 3.1 Pro 전부에서 코드 생성 도중 갑자기 400 에러 나던 문제 — 전부 잡았습니다.**

---

## 사용자 체감 변화

| 증상 (v1.0.2) | 결과 (v1.0.3) |
|---------------|---------------|
| Gemini 선택 시 질문도 안 하고 채팅 버블에 코드 박힘 | ✅ 정상적으로 task → 질문 → 코드 흐름 |
| GPT-5.5 사용 중 갑자기 "API request failed: 400" | ✅ 발생 안 함 |
| Claude 도 복합 작업 (Excel 내보내기, Phase 시각화) 중 같은 400 | ✅ 발생 안 함 |
| Gemini 로 codegen 진입하면 두 번째 turn 에서 "thought_signature" 에러 | ✅ 발생 안 함 |
| Settings 에서 모델 골라도 어떤 게 빠른지 모름 | ⚡⚡⚡ ~ ⚡ 속도 글리프로 한 눈에 비교 |

---

## 핵심 수정 사항 (BIBIM-001 ~ 007)

### 1. Anthropic 400 — `tool_result.name` 거부 (BIBIM-001)
Claude 가 코드 생성 중 도구 (예: `search_revit_api`, `run_roslyn_check`) 호출하고 결과 받아 다음 turn 진입할 때, 우리가 도구 결과 블록에 박은 `name` 필드를 Anthropic 의 strict schema 가 reject. **두 번째 호출부터 무조건 400**, tool loop 마비.

→ `AnthropicProvider` 가 전송 직전에 `tool_result.name` 을 자동 strip. Gemini adapter 쪽은 영향 없음.

### 2. OpenAI 400 — "messages must contain the word 'json'" (BIBIM-002)
GPT-5.5 선택 시 task planner LLM 호출 첫 시도부터 400. OpenAI Responses API 의 `text.format=json_object` 모드는 input 메시지에 영문 "json" 단어가 literal 하게 등장해야 하는 quirk. instructions (시스템 프롬프트) 에만 있으면 거부.

→ `BuildPlannerInput` 끝에 `[Output format: respond with JSON only — no markdown, no commentary.]` 한 줄 박음. Gemini 한테도 instruction 강화 부수효과.

### 3. Gemini 모델 변경 (BIBIM-003)
이전 빌드의 `gemini-3.1-pro-preview-customtools` 변형은 agent + custom tool 워크플로우 전용. 함수 호출 우선 동작 때문에 **JSON 출력 모드를 silently 무시** — planner 가 JSON 안 뱉어서 fallback chat 으로 빠져 코드가 채팅 버블에 박힘.

→ vanilla `gemini-3.1-pro-preview` 로 교체 (Google 공식 권장: tool calling 50% 미만이면 vanilla 사용). 기존 사용자 config 의 옛 모델 ID 는 **다음 launch 시 자동 마이그레이션** + `.bak` 백업.

### 4. 설정 패널 모델 셀렉터 — 응답 속도 글리프
4개 모델 중 어느 게 빠른지 한 눈에 비교할 수 있도록 ⚡ 아이콘 추가. 마우스 올리면 한국어/영문 설명 툴팁.

| 모델 | 속도 글리프 |
|------|-------------|
| Claude Sonnet 4.6 | ⚡⚡⚡ 빠름 |
| Claude Opus 4.7 / GPT-5.5 | ⚡⚡ 보통 |
| Gemini 3.1 Pro | ⚡ 느림 (추론 시간이 길 수 있음) |

### 5. Gemini `thoughtSignature` echo (BIBIM-006)
Gemini 3.x thinking 모델은 함수 호출 응답에 `thoughtSignature` (추론 상태 토큰) 박아 보내고, 다음 turn 에 그 함수 호출을 history 로 다시 보낼 때 echo 해야 함. 빠지면 400 `Function call is missing a thought_signature`. v1.0.2 는 응답에서 signature 캡처 안 하고 echo 도 안 함 — Gemini 로 codegen tool loop 진입하면 두 번째 turn 에서 무조건 깨짐.

→ `GeminiProvider` 가 응답에서 `thoughtSignature` 캡처해 stash, 다음 호출에서 그대로 echo.

### 6. max_tokens 잘림 + tool_use 처리 결함 (BIBIM-007)
**핵심 결함**. 모델이 reasoning + 도구 호출 + 설명을 한 응답에 emit 하다가 max_tokens 한도 (4096) 도달하면 텍스트가 잘림. tool_use 블록은 잘리기 전에 완성됐는데, 우리 orchestrator 가 "잘렸으니 이어 써" user 메시지 박음. Anthropic / OpenAI 양쪽 다 **tool_use → tool_result 짝이 없으면 400 reject** 라 다음 turn 죽음.

→ 두 단계 fix:
- `max_tokens` 4096 → 8192 (헤드룸 확보 — `max_tokens` 는 청구액 아니라 ceiling이라 비용 영향 0).
- response 파싱 시 content 에 tool_use 블록이 하나라도 있으면 stop_reason 을 "tool_use" 로 강제 → 도구 실행 후 정상 pairing 형성.

이 fix 가 들어간 후 사용자 테스트 매트릭스의 거의 모든 시나리오 통과.

---

## 영향 받는 사용자

| 프로바이더 | v1.0.2 사용 시 영향 | v1.0.3 |
|----------|---------------------|--------|
| Anthropic Claude | 복합 작업 중 가끔 400 (BIBIM-001 + 007) | 안정 |
| OpenAI GPT-5.5 | task planner 첫 호출부터 깨짐 (BIBIM-002) + 복합 작업 중 400 (007) | 안정 |
| Google Gemini 3.1 Pro | task planner 깨짐 (003) + codegen tool loop 깨짐 (006) + 복합 작업 중 400 (007) | 안정 |

세 프로바이더 모두 **안정 사용 가능 상태로 회복**. v1.0.2 멀티 프로바이더의 진짜 출시.

---

## 자동 마이그레이션

기존 v1.0.2 사용자가 처음 v1.0.3 실행 시:
- `gemini-3.1-pro-preview-customtools` 모델 ID 가 저장되어 있으면 → 자동으로 `gemini-3.1-pro-preview` 로 변경 + `rag_config.json.bak` 백업 생성
- 사용자가 Settings 에서 다시 선택할 필요 없음
- Anthropic / OpenAI 사용자는 영향 없음

debug log 의 한 줄 마이그레이션 신호:
```
[ConfigService]: Migrated saved model id 'gemini-3.1-pro-preview-customtools' → 'gemini-3.1-pro-preview' (rewrote rag_config.json).
```

---

## 빌드 / 배포

| 빌드 타겟 | 결과 |
|----------|------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

---

## 요구사항

- Autodesk Revit 2022 이상
- 다음 중 하나 이상의 API 키:
  - [console.anthropic.com](https://console.anthropic.com/) (Claude)
  - [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT-5.5)
  - [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini 3.1 Pro)

## 소스
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
