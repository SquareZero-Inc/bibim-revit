# BIBIM v1.1.0

**릴리즈일**: 2026-05-16

> **자체 호스팅 LLM 지원 릴리즈** — Ollama / LM Studio / vLLM / llama.cpp 같은 OpenAI 호환 서버를 BIBIM에 직접 연결. NDA 환경, 데이터 보안 요건이 있는 기업, 토큰 비용이 부담되는 헤비 유저를 위한 새 옵션.

---

## 한 줄 요약

**자체 LLM 서버 주소만 입력하면 BIBIM이 그 안에서 동작합니다. 클라우드 API 안 거치고, 데이터 외부 유출 없이, 토큰 비용 0으로.**

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

- 모델 자동 감지 실패 시 친절한 안내 — "Settings → Advanced → 모델명 수동 override 필드를 채워주세요" 메시지 (이전엔 의미 불분명 404 에러)
- API key 라벨 + 툴팁 정확화 — "API key (Bearer token)" 명시, HTTP `Authorization: Bearer` 헤더로 전송됨을 첫 줄에 명시
- Settings 패널 시각 밀도 약 50% 감소 (returning user 기준)

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
