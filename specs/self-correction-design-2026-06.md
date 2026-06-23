# Runtime Self-Correction (안 A+) — 설계 문서 (2026-06, Opus 4.8)

> 목표: LLM이 추측으로 짠 코드가 런타임에 실패(cast 크래시 / 0개 변경 / 전량 skip /
> **멀티스텝 작업 누락**)할 때, 그 결과를 LLM에 피드백해 자가수정시킨다. 케이스별
> 프롬프트 룰("Door Width는 type") 대신 **관찰-수정 메커니즘**으로 무한 케이스에 대응.
>
> 상태: **승인됨 (2026-06-23)**. Phase 1부터. 각 Phase 독립 빌드/검증/커밋 + feature flag 롤백.
> main 머지 금지 — feature/self-correction 브랜치에서만.

## 안 A+ = 안 A(강제 휴리스틱) + Tier 2(멀티스텝 의미 self-judge)

속도/정확도/비용 정량 비교 후 A+ 채택. 결정 근거: **로그가 모델의 "도구 결과 무시"를 증명**
(Opus가 get_element_parameters 결과를 무시) → 자율(안 B/D)이 아니라 **강제**가 정확도 하한을
보장해야 함. 단 단순 휴리스틱(exception/0개)은 **작업 누락(26-0605: 뷰 복제는 했는데 파라미터
입력 빼먹음)**을 못 잡음 → 멀티스텝 작업엔 의미 self-judge(Tier 2) 추가.

### 2-Tier 판정
```
Tier 1 — 휴리스틱 (모든 write 작업, 싸다, dry-run 결과만으로):
  · 런타임 exception (Success==false)        → 재생성
  · Write인데 AffectedElementCount==0         → 재생성 (1회 한정)
  · Output/Log에 "⚠" prefix (전량 skip)       → 재생성 (1회 한정)
  · AffectedElementCount > ScaleGuard(500)    → 스킵 (대량작업 dry-run 반복 방지)

Tier 2 — 멀티스텝 완전성 (작업 누락 방지):
  · [구현됨] codegen 프롬프트에 MULTI-STEP COMPLETENESS 룰: 다중 액션 요청이면 모든
    단계 수행 + 끝에 단계별 체크리스트 ctx.Log + 누락 발견 시 스스로 보완. judge LLM
    없이 코드가 자가점검. 케이스별 지식이 아니라 일반 메타 원칙(1블록, 무한확장 X).
  · [후속 옵션, 미구현] 별도 judge LLM(결과+요청을 주고 "충족?" 판정)은 dry-run 결과
    만으로의 의미검증이 코드 로깅 품질에 의존 + judge 오판 리스크 + 비용 → 효과 불확실해
    보류. 멀티스텝 완전성 프롬프트가 실기기에서 부족하면 그때 추가.
```

## 실측 근거 (로그)
| 작업 | codegen | dry-run | 비고 |
|---|---|---|---|
| 문너비 | 93초/4턴/~12k in | 20초 | Width=type, 인스턴스 Set 0개 (Tier1 0개→재생성) |
| 방화문 919개 | 91초 | **232초** | 스케일가드 필수 (이게 2번 돌면 8분) |
| 치수선 | — | ~1초(0개) | 외곽 의도→중심 fallback→거리0 (Tier1 0개→재생성, 자동 N회) |
| 뷰복제+파라미터 | — | 성공 | **작업 누락 — Tier1 못잡음 → Tier2 self-judge 필수** |

---

## 1. 현재 구조 (정독 결과 — 근거)

### codegen 흐름
```
GenerateCodeFromTaskAsync (BibimDockablePanelProvider.cs:3601)
  → llm.GenerateWithToolsAsync(...)            ← tool loop, maxTurns:15
       내부: for turn in maxTurns:
         LLM 호출 (tools)
         ├ stop_reason==tool_use → 도구 실행(run_roslyn_check 등) → tool_result 피드백 → continue
         └ stop_reason==end_turn → code 추출 → _compiler.Compile(code)
              ├ Success → return (코드 확정)                       ← [삽입 지점 A]
              └ Fail → 컴파일에러 피드백 → continue (재생성)        ← [기존 self-correction, 대칭 모델]
  → [루프 종료, 코드 확정 후]
  → ExecuteCompiledCodeAsync(isDryRun:true)    ← dry-run, 루프 밖 1회 (line 3764)
  → task.Stage = PreviewReady, 사용자에게 결과 표시
```

### 핵심 발견
1. **컴파일 self-correction이 이미 있다** (LlmOrchestrationService.cs:389-408): 컴파일 실패 →
   `BuildCompileErrorFeedback` → messages에 추가 → continue. 런타임 버전은 이 **대칭 위치**(line 380
   `if(compileResult.Success)` 직후)에 끼운다.
2. **dry-run은 commit+group-rollback** (log "[DryRun] Affected: N (commit+group-rollback pattern)").
   여러 번 실행해도 롤백되므로 안전. 부작용 작업(파일)은 FileOutputRules의 `_BIBIM_TEST` 접미사로 격리.
3. **의존성 분리**: dry-run 실행(`ExecuteCompilationAsync` → `BibimApp.ExecutionHandler.Enqueue` +
   ExternalEvent)은 BibimDockablePanelProvider에 있고, LlmOrchestrationService는 이를 모른다.
   → **콜백 주입**으로 해결 (toolExecutor와 동일 패턴, GenerateWithToolsAsync line 236).
4. **ExecutionResult 필드**: Success, ErrorMessage, AffectedElementCount, Output, HasExecutionLogs,
   ExecutionLogs, HasRevitWarnings, RevitWarnings. 판정에 충분.

---

## 2. 설계

### 2.1 의존성 주입 — 콜백
GenerateWithToolsAsync에 optional 파라미터 추가 (null이면 기존과 100% 동일 동작):
```csharp
Func<CompilationResult, CancellationToken, Task<DryRunOutcome>> dryRunValidator = null
```
BibimDockablePanelProvider가 `ExecuteCompilationAsync(compileResult, isDryRun:true)` 를 래핑해 주입.
LlmOrchestrationService는 Revit을 여전히 모른다 (분리 유지).

### 2.2 DryRunOutcome (LlmOrchestrationService 신규 타입)
```csharp
public sealed class DryRunOutcome
{
    public bool ShouldRegenerate;   // true면 루프가 피드백 추가 후 continue
    public string FeedbackText;     // LLM에 줄 런타임 검증 메시지
    public bool Ran;                // dry-run을 실제 실행했나 (가드로 스킵됐으면 false)
}
```
판정 로직은 **콜백(BibimDockablePanelProvider) 쪽**에 둔다 — Revit 도메인 지식(AffectedElementCount
의미, Write 여부)이 거기 있으므로. LlmOrchestrationService는 ShouldRegenerate만 보고 분기.

### 2.3 삽입 지점 (LlmOrchestrationService.cs:380)
```csharp
if (compileResult.Success)
{
    // ── NEW: 런타임 검증 (콜백 주입 시) ──
    if (dryRunValidator != null && runtimeRetries < maxRuntimeRetries)
    {
        OnStatusUpdate?.Invoke("Validating (preview)...");
        var outcome = await dryRunValidator(compileResult, ct);
        if (outcome.ShouldRegenerate)
        {
            runtimeRetries++;
            messages.Add(new JObject { ["role"]="assistant", ["content"]=content });
            messages.Add(new JObject { ["role"]="user", ["content"]=outcome.FeedbackText });
            Logger.Log("LlmOrchestration",
                $"rid={requestId} runtime validation failed (retry {runtimeRetries}/{maxRuntimeRetries}), regenerating");
            continue;
        }
    }
    result.Success = true;
    return result;   // (기존)
}
```
- `runtimeRetries` / `maxRuntimeRetries`: 루프 상단에 카운터 추가.
- 결정적: dry-run 재생성도 **기존 maxTurns(15) 안에서** 돈다 → 무한루프 불가.

### 2.4 판정 기준 (콜백 = BibimDockablePanelProvider)
dry-run 실행 후 ExecutionResult로 판정:

| 조건 | ShouldRegenerate | 근거 |
|---|---|---|
| `Success == false` (런타임 exception, e.g. InvalidCastException) | **true** | 명백한 실패. 케이스4 |
| Write task + `AffectedElementCount == 0` | **true (단 1회)** | "0개 변경" 의심. 케이스2. 진짜 0개일 수 있어 1회만 |
| Output/Log에 "⚠" prefix (HONEST REPORTING) | **true (단 1회)** | 전량 skip 의심 |
| `AffectedElementCount > ScaleGuardThreshold` (기본 500) | **false** | 스케일 가드 — 대량작업은 dry-run 반복이 비싸니 사용자 판단에 맡김. 케이스3 연계 |
| 정상 (Success + affected>0) | false | 통과 |

"의심" 케이스(0개/skip)는 `maxRuntimeRetries=1` 로 제한 → 한 번 고칠 기회 주고 안 되면 사용자에게 보여줌.
"명백한 실패"(exception)도 같은 카운터 사용 (간결성). 즉 **런타임 재생성은 작업당 최대 1회** (Phase 3 기준).

### 2.5 피드백 메시지 (콜백이 생성)
```
[RUNTIME VALIDATION] Your code compiled and ran as a preview (dry-run, rolled back).
Result:
- Status: {Success|FAILED}
- Runtime error: {ErrorMessage}                    (있을 때만)
- Affected elements: {AffectedElementCount}
- Execution log:
{ctx.Log 출력, 최대 2KB}

This does not look right ({reason}). Diagnose the ROOT CAUSE and regenerate the code.
Use tools (get_element_parameters to check if a parameter is type-level/read-only,
get_project_levels, etc.) to verify assumptions instead of guessing. This is your
ONLY runtime-correction attempt — make it count. If you are certain the result IS
correct (e.g. there genuinely are 0 matching elements), return the same code.
```
- `{reason}`: "0 elements were changed" / "a runtime exception occurred" / "all elements were skipped".

### 2.6 성능 가드 (정직한 리스크 대응)
- **스케일 가드** (2.4): affected > 500이면 self-correction 스킵. 919개 같은 대량작업이 dry-run을
  두 번 (8분+) 도는 재앙 방지. 임계값은 config.
- **dry-run 1회 비용**: 성공 시나리오는 dry-run 1회 (지금과 동일, 추가 비용 0). 재생성은 실패/의심
  시에만 +1회. 즉 **잘 되는 작업엔 비용 증가 없음**.
- **재생성 시 추가**: dry-run 1회 + LLM 1회 + dry-run 1회 ≈ 작업당 최대 1회분 추가. maxRuntimeRetries=1로 상한.

### 2.7 Feature flag (rag_config.json `validation` 섹션)
```json
"self_correction_enabled": true,        // 마스터 스위치 (Phase별 점진 활성)
"self_correction_max_retries": 1,       // 런타임 재생성 상한
"self_correction_scale_guard": 500      // 이 이상 affected면 스킵
```
ConfigService.RagConfig에 필드 추가. 중간 검증 시 끄고 켤 수 있어 **안전한 롤백** 보장.

---

## 3. 단계적 구현 (각 Phase 독립 빌드/리뷰/검증)

### Phase 1 — 인프라 (행동 변화 0, 100% 안전)
- `DryRunOutcome` 타입 정의 (LlmOrchestrationService)
- GenerateWithToolsAsync에 `dryRunValidator` 파라미터 + `runtimeRetries`/`maxRuntimeRetries` 추가
- 삽입 지점 코드 추가 (2.3)
- BibimDockablePanelProvider: GenerateCodeFromTaskAsync에서 콜백 **null 전달** (아직 비활성)
- config 필드 추가 (읽기만, 미사용)
- **검증**: 빌드 0/0 + 기존 동작 100% 동일 (콜백 null이라 분기 안 탐). 리뷰.

### Phase 2 — exception 케이스만 (가장 안전한 self-correction)
- 콜백 실제 주입. 판정은 **`Success == false` (런타임 exception)일 때만** ShouldRegenerate.
- 0개/skip "의심" 케이스는 아직 false (보수적 시작).
- feature flag로 보호.
- **검증**: 케이스4(외벽 cast 크래시) → dry-run InvalidCastException → 재생성 → `.OfClass` 추가 →
  통과 확인. 빌드/리뷰/실기기 테스트.

### Phase 3 — "의심" 케이스 (0개/전량skip) + 스케일 가드
- 판정에 AffectedElementCount==0, "⚠" 패턴 추가 (maxRuntimeRetries=1).
- 스케일 가드 (affected>500 스킵).
- **검증**: 케이스2(문 너비 0개) → 재생성 시도. 단 입력 오염(Phase 4) 병행 필요.
  케이스3(919개) → 스케일 가드로 self-correction 스킵 확인. 빌드/리뷰/테스트.

### Phase 4 — 입력 오염 제거 (병행 가능, self-correction과 독립)
- CategoryQuestionTemplates `parameter` 라인에서 `instance vs type` 제거
  (구현 디테일이라 사용자에게 묻지 않음 → 케이스2의 오염 원천 차단).
- planner 룰: "값 변경 시 instance/type 판단은 codegen이 도구로 결정, 사용자에게 묻지 않음".
- **검증**: 케이스2 완성 (Phase 3 + 4 합쳐야 완전 해결). 빌드/리뷰/테스트.

### Phase 5 — 프롬프트 다이어트 (메커니즘이 지식 대체)
- self-correction 안정 확인 후, CodeGenSystemPrompt의 고위험 API 폴백 룰(RotateElement/
  NewLoftForm/face.Project 3종, ~동일 패턴)이 self-correction으로 커버되는지 검토 → 축소.
- **보수적**: 측정 후 결정. 무리하게 안 지움.

---

## 4. 성능 측정 포인트 (Phase 2~3 검증 시)
- dry-run 1회 소요시간 (작업 규모별: 10개 / 100개 / 900개)
- self-correction 발동률 (전체 작업 중 재생성 트리거 %)
- 재생성 시 추가 wall-clock + 토큰
- 스케일 가드 작동 (대량작업에서 스킵 확인)
- 잘 되는 작업에 비용 증가 0인지 (성공 시 dry-run 1회 유지 확인)

---

## 5. 미해결 / 트레이드오프 (정직)
- **"성공인데 의미가 틀림"은 못 잡는다**: 0개·exception은 잡지만, 엉뚱한 100개를 바꿔도 "성공"이면
  통과. 의미 검증은 self-correction 범위 밖 (사용자 preview가 최종 방어선).
- **0개 오판**: 진짜 0개인 정상 작업도 1회 재생성 낭비. maxRetries=1로 비용 제한, 두 번째는 통과.
- **대량작업**: 스케일 가드로 self-correction 스킵 → 케이스3은 self-correction이 아니라 가드로 "경고"만.
  완전 해결 아님 (성능 본질 문제는 별도).

---

## 6. 브랜치
`feature/ux-flow` 위에서 분기 → `feature/self-correction`. (question-flow+ux-flow 포함된 최신 위.
전부 main 미머지 상태 유지, 머지 순서는 사용자 결정.)
