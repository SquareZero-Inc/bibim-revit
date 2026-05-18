# BIBIM v1.1.0

**릴리즈일**: 2026-05-18

> **자체 호스팅 LLM 지원 릴리즈** — Ollama / LM Studio / vLLM / llama.cpp 같은 OpenAI 호환 서버를 BIBIM에 직접 연결. NDA 환경, 데이터 보안 요건이 있는 기업, 토큰 비용이 부담되는 헤비 유저를 위한 새 옵션.

---

## 한 줄 요약

**자체 LLM 서버 주소만 입력하면 BIBIM이 그 안에서 동작합니다. 클라우드 API 안 거치고, 데이터 외부 유출 없이, 토큰 비용 0으로.**

v1.0.2가 클라우드 3종 (Claude / GPT / Gemini)을 BYOK로 열었다면, **v1.1.0은 self-hosted까지 열어 BIBIM의 BYOK 약속이 진짜로 완성**됩니다. 동시에 코드 생성 정확도·시작 속도·보안·UX 전 영역에서 polish가 한꺼번에 들어갔습니다.

---

## 이번 릴리즈의 핵심 (한눈에)

| 영역 | 변화 | 누가 체감 |
|------|------|---------|
| 🆕 **Self-hosted LLM 지원** | Ollama / LM Studio / vLLM / llama.cpp 등 OpenAI 호환 서버 직접 연결. URL 한 줄 + 자동 모델 감지 | NDA 환경 · 사내망 GPU 운영 기업 · 토큰 비용 부담 헤비 유저 |
| ✅ **코드 생성 정확도 ↑** | BIBIM001 (Transaction 필수) 분석기가 `Execute`라는 메서드명 만나면 단락 처리하던 false-positive 픽스 | **모든 사용자** — 코드 생성이 더 견고해짐 |
| ⚡ **세션 시작 1~2초 단축** | `RoslynCompilerService` 진짜 싱글톤화 + 로컬 RAG 캐시 lock-free fast-path | 헤비 사용 패턴 (자주 panel 띄우는 경우) |
| 🔒 **자동 업데이트 보안 강화** | host 화이트리스트 (`github.com` / `objects.githubusercontent.com`만 허용) + 10분 타임아웃 + 부분 다운로드 정리 | 모든 사용자 — 자동 업데이트 안전성 ↑ |
| 🎨 **Settings 화면 전면 개편** | 활성 모델 한눈 chip · 등록된 키 섹션 자동 접힘 · Local LLM 단일 entry로 통합 | 재방문 사용자 — 시각 부담 약 50% 감소 |
| 🧹 **코드 헬스** | `#if NET48` 조건부 컴파일 −432줄 · 죽은 클래스 제거 · 단위 테스트 64/64 통과 | 향후 빌드 안정성·기여자 학습 곡선 |

---

## 주요 변경사항

### 1. 자체 호스팅 로컬 LLM 지원 (NEW)

회사 사내망 GPU 서버, 클라우드 GPU 임대 (RunPod / Vast.ai 등), 개인 워크스테이션 위에 띄운 **OpenAI 호환 LLM 서버**를 BIBIM에 직접 연결할 수 있습니다.

지원되는 서버:
- **Ollama** (가장 일반적, 개인 워크스테이션 권장)
- **LM Studio** (GUI 환경 선호 시)
- **vLLM** (production / 사내 GPU 클러스터 표준)
- **llama.cpp server** (가벼운 CPU/Apple Silicon 환경)
- 그 외 OpenAI `/v1/chat/completions` 형식 호환 서버 (OpenRouter 등 게이트웨이 포함)

**설정 방법은 클라우드 API 키와 동일 수준의 마찰**:

1. Settings → Local LLM (Self-hosted) 섹션
2. 서버 URL 한 줄 입력 (예: `http://localhost:11434/v1`)
3. **연결 테스트 + 저장** 클릭
4. 서버에 설치된 모델이 자동 감지되어 드롭다운에 노출
5. 끝 — 채팅 즉시 가능

### 2. 추천 모델 — BIBIM 자체 검증

OpenRouter 환경에서 Revit 2024 smoke matrix로 오픈소스 모델을 직접 테스트했습니다. **Tool calling 지원 + 30B 이상** 모델을 권장:

| 모델 | 4-bit VRAM | 비고 |
|------|------|------|
| **Gemma 4 26B A4B IT** ⭐ | ~16GB | BIBIM 검증 최고 점수, RTX 4090 OK |
| Codestral 2508 (22B) | ~14GB | 가장 빠름. 간단 작업 한정 |
| Llama 3.3 70B Instruct | ~40GB+ | 24GB 단일 GPU 불가 (A100 / 이중 3090급 필요) |

⚠ 작은 모델 (≤7B)은 Revit API tool calling 안정성이 낮아 코드 생성 실패율이 높습니다. 권장 모델 외 사용은 가능하지만 결과 품질을 보장하지 않습니다.

### 3. 인증된 self-hosted 환경 (Bearer token)

다음 시나리오에 대비한 **API key (Bearer token)** 필드를 Advanced 섹션에 추가:

- vLLM을 `--api-key <토큰>` 옵션으로 띄운 경우
- nginx reverse proxy 뒤에 인증 추가된 사내망 LLM
- RunPod / Vast.ai 같은 클라우드 GPU 임대 endpoint
- Tailscale / Cloudflare Tunnel 게이트웨이 뒤의 서버

HTTP 요청에 `Authorization: Bearer <값>` 헤더로 자동 전송. **인증 없는 기본 Ollama / LM Studio 환경에선 비워두면 됨**.

### 4. 설정 화면 전면 개편

- **활성 모델 chip** — 패널 상단에 "지금 어떤 모델·어떤 인증으로 동작 중인지" 한 줄로 노출 (예: `Active: Claude Sonnet 4.6 · sk-ant-...Ab3c`).
- **등록된 키 섹션 자동 접힘** — 키가 이미 설정된 프로바이더 섹션은 `✓ Key configured: ...Ab3c [교체]` 한 줄로 축약. 재방문 사용자의 시각 부담 절반 이하로 감소.
- **로컬 LLM 단일 entry** — 모델 선택기에서 기존 3개로 나뉘어 있던 OSS 모델 (Gemma / Llama / Codestral)이 단일 **Local LLM (Self-hosted)** entry로 통합. 활성 모델 이름은 동적으로 노출 (`Active: gemma2:27b`).
- **섹션 순서 재배치** — 가이드 → 현재 setup → 키 입력 → Model 선택 → 피드백 순으로, 실제 설정 흐름과 일치.

---

## 자동 마이그레이션

기존 v1.0.2 / 1.0.3 사용자가 v1.1.0를 처음 실행할 때:

- `claude_model` 값이 옛 OpenRouter 형식 (예: `google/gemma-4-26b-a4b-it`)이면 → 자동으로 `"local"`로 변경, 모델명 단편은 `local.model_name`에 보존
- `rag_config.json.bak` 백업 자동 생성

→ **기존 설정 그대로 동작**. 재설정 필요 없음.

debug log에 한 줄 마이그레이션 신호:
```
[ConfigService]: Migrated saved model id 'google/gemma-4-26b-a4b-it' → 'local' (local.model_name = 'gemma-4-26b-a4b-it', rewrote rag_config.json).
```

---

## 버그 수정 / 개선

### Local LLM 폴리시

- **인증 없는 Local LLM 환경의 채팅 차단 해제.** `user_message` 핸들러의 사전 검사가 `ApiKey`가 비어있으면 무조건 차단했는데, 인증 없는 Ollama / LM Studio 기본 설치에서는 키가 비어있는 게 정상 (인증 없음 = 빈 키가 맞음). 검사가 이제 provider별로 분기 — Local은 `LocalServerUrl`을, 클라우드는 `ApiKey`를 검증. Local에 대한 에러 메시지도 URL 안내로 정정해서 엉뚱하게 키 입력하러 가지 않도록 함.
- 모델 자동 감지 실패 시 친절한 안내 — "Settings → Advanced → 모델명 수동 override 필드를 채워주세요" 메시지 (이전엔 의미 불분명 404 에러).
- API key 라벨 + 툴팁 정확화 — "API key (Bearer token)" 명시, HTTP `Authorization: Bearer` 헤더로 전송됨을 첫 줄에 명시.
- Settings 패널 시각 밀도 약 50% 감소 (returning user 기준).

### 보안 강화

- **자동 업데이트가 이제 host 화이트리스트 + 시간 제한 적용.** `download_update` 핸들러가 `https://github.com` / `https://objects.githubusercontent.com` URL만 허용 (GitHub Releases 침해나 WebView 하이재킹 가상 시나리오에 대한 defense-in-depth), 본문 스트림 전체에 10분 `CancellationTokenSource` 적용, 타임아웃 / 네트워크 실패 시 부분 다운로드 정리. 진행 상태 메시지 typed화 (`timeout` / `network` / `untrusted_url`) — UI에서 에러 종류별 안내 분기 가능.

### 분석기 정확도

- **BIBIM001 (Transaction 필수 검사)가 생성 코드의 Transaction 누락 버그를 더 이상 숨기지 않음.** 기존 휴리스틱은 syntax tree를 거슬러 올라가다 `Execute`라는 이름의 메서드를 만나면 즉시 "OK"로 단락 처리했는데 — "`BibimExecutionHandler`가 `Execute`를 Transaction으로 감싼다"는 잘못된 전제 위에 있었음. 실제론 안 감쌈 (`RunCommit`은 outer wrapper 없음; `RunDryRun`은 `TransactionGroup`을 쓰는데 modification API 직접 호출 불가). bare `doc.Delete(id)`가 `Execute` 안에서 분석을 통과해 런타임 실패나 문서 불일치를 야기할 수 있었음. 이제 진짜 `Transaction` `using` 조상을 요구하고 `TransactionGroup` / `SubTransaction`은 false-positive로 명시적 거부 (regex). Roslyn retry가 보통 자동 수정하므로 사용자는 "코드 생성이 약간 더 견고해진" 정도로 체감.

### 성능

- **`RoslynCompilerService`를 패널 수준 진짜 싱글톤화.** 생성자가 `AppDomain.GetAssemblies()`를 스캔하는데 (어셈블리당 ~100-300ms), `BibimDockablePanelProvider`의 6개 호출 지점이 `BibimApp.OnStartup`이 등록한 프로세스 전역 인스턴스 무시하고 매번 `new`를 부르고 있었음. 세션당 누적 시작 작업 ~1-2초 회수.
- **`LocalRevitRagService` 캐시 fast-path가 더 이상 lock을 잡지 않음.** `search_revit_api`용 BM25 엔진은 세션당 한 번 build lock 안에서 만들어지지만, 이후 캐시된 읽기는 lock 자체를 스킵 (volatile read + double-check). 같은 LLM 턴에서 dispatch되는 병렬 `search_revit_api` tool_use 블록들이 더 이상 직렬화되지 않음.

### 로깅

- **로그 경로 이동**: `%USERPROFILE%\bibim_v3_debug.txt` → `%APPDATA%\BIBIM\logs\bibim_debug.txt`. 애드온의 나머지 저장소 레이아웃 (`%APPDATA%\BIBIM\`)과 일치, 홈 디렉토리 루트에 orphan 파일 남기는 동작 종료. 기존 로그는 마이그레이션 안 함 — 서포트 자료로 필요할 수 있어 그대로 보존.
- **진단 보고서에 `LogFile` 체크 추가** — 현재 로그 크기를 표시하고, 구버전 `bibim_v3_debug.txt`가 남아있으면 안내.

### 코드 헬스 (사용자 비가시)

- `#if NET48` 조건부 직렬화 어트리뷰트 제거 — `CodeLibraryModels.cs`, `SessionModels.cs`, `TaskFlowModels.cs`에서 112 occurrences. `JsonHelper`가 양쪽 타겟 모두 Newtonsoft.Json을 쓰기 때문에 net8 빌드에서 `[JsonPropertyName]` 분기가 런타임에 무시되고 있었음. 단일 `[JsonProperty]`가 양쪽 다 적용. **−432줄 무가치 조건부 컴파일.**
- `ConversationContextManager` 삭제 — 122줄짜리 클래스인데 참조 0개.
- `GeminiProvider.SendStreamingAsync`가 이제 SSE 청크에서 `functionCall` + `thoughtSignature`를 파싱해 `StreamResult.ToolUseBlocks`에 저장 — 향후 streaming + tools 와이어드 시점을 위한 forward compatibility.
- `BibimApp.OnShutdown`이 이제 `DocumentChanged` unsubscribe가 던지는 예외 메시지 로깅 (이전엔 silently swallow).
- `*.tsbuildinfo`를 `.gitignore`에 추가해 TypeScript 증분 빌드 캐시가 더 이상 `git status`에 안 뜨도록.

### UX / i18n

- 채팅 메시지의 피드백 버튼이 이제 `👍 도움 됨` / `👎 도움 안 됨`로 표시 (이전엔 "Up" / "Down" 영어 그대로 — i18n 키는 이미 있었는데 툴팁에만 적용되고 버튼 라벨엔 누락된 상태였음).
- 다중 선택 질문 카드의 "Select all that apply" 문구가 이제 i18n에서 읽어옴 (이전엔 영어 하드코딩) — 한국어 빌드에선 "해당되는 항목을 모두 선택하세요"로 표시.

64 / 64 단위 테스트 통과 (BIBIM001 휴리스틱 픽스 검증용 신규 3개 추가; 잘못된 동작을 codify하던 기존 테스트 1개를 정정된 동작 assert하도록 재작성).

---

## 영향 받는 사용자

| 환경 | v1.1.0 |
|------|------|
| 클라우드 BYOK (Anthropic / OpenAI / Gemini) 사용자 | 변경 없음 — 기존 키 그대로 동작 |
| Self-hosted LLM 인프라 보유 | **새 옵션** — Local LLM 섹션에서 즉시 연결 |
| NDA / 데이터 외부 유출 제약 환경 | **새 옵션** — 사내망 LLM으로 BIBIM 활용 가능 |

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

- Autodesk Revit 2022 이상 (Windows)
- 다음 중 하나 이상:
  - 클라우드 API 키 ([console.anthropic.com](https://console.anthropic.com/) (Claude) / [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT) / [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini))
  - **NEW**: 자체 호스팅 OpenAI 호환 LLM 서버 (Ollama / LM Studio / vLLM / llama.cpp 등) — Tool calling 지원 + 30B 이상 모델 권장

## 소스

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
