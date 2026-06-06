# 로컬 모델로 실행 (API 키 없이)

BIBIM은 내 PC에서 완전히 로컬로 도는 언어 모델로 구동할 수 있습니다 — **API 키 없음, 질의당 비용 없음, 어떤 것도 PC 밖으로 나가지 않음.** 클라우드 프로바이더에 가입하기 싫다면 가장 쉬운 시작 방법입니다.

BIBIM은 **OpenAI 호환 `/v1` API**를 제공하는 서버라면 무엇이든 연결합니다 — **Ollama, LM Studio, vLLM, llama.cpp** 등.

## 1. 로컬 서버 실행

하나를 고르세요:

**Ollama** — [ollama.com](https://ollama.com)에서 설치한 뒤 코드 작성이 가능한 모델을 받습니다:

```bash
ollama pull <모델-이름>
```

Ollama는 `http://localhost:11434/v1`에 OpenAI 호환 API를 엽니다.

**LM Studio** — [lmstudio.ai](https://lmstudio.ai)에서 설치하고 모델을 받은 뒤, 로컬 서버를 시작합니다(Developer 탭 → **Start Server**). `http://localhost:1234/v1`에서 제공됩니다.

## 2. BIBIM이 가리키게 하기

1. BIBIM 패널 → 톱니바퀴(⚙) → **설정**을 엽니다.
2. **Local LLM (Self-hosted)** 섹션에서 **Server URL**을 `/v1`로 끝나게 입력합니다:
   - Ollama → `http://localhost:11434/v1`
   - LM Studio → `http://localhost:1234/v1`
3. **Model name**은 비워두면 BIBIM이 자동 탐색합니다(`/v1/models`를 조회해 첫 모델 사용). 또는 서버가 기대하는 정확한 이름을 입력합니다.
4. API 키 칸은 비워둡니다 — 로컬 서버는 기본적으로 키가 필요 없습니다. (인증 프록시·터널 뒤에 서버를 두었을 때만 입력하세요.)
5. **저장**을 누른 뒤, 모델 선택기에서 **Local LLM (Self-hosted)**를 고릅니다.

## 3. 알아둘 점

- **품질은 모델을 따라갑니다.** 크고 코드 능력이 좋은 모델일수록 Revit 코드 품질이 눈에 띄게 좋아집니다.
- **속도가 트레이드오프입니다.** 로컬 모델은 보통 클라우드 프런티어 모델보다 느립니다 — 비용 0·완전 비공개의 대가입니다.
- **언제든 전환.** 로컬 설정을 잃지 않고 설정에서 클라우드 모델로 바꿀 수 있습니다.
