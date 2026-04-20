# Slack AI Team Bot

Slack 이벤트를 수신해 OpenAI(GPT-4o)로 역할별 AI 팀 응답을 생성하고, 정기 회의·토론·개발 파이프라인 시나리오를 자동 실행하는 ASP.NET Core 백엔드입니다.

---

## 1. 프로젝트 개요 (Overview)

### 프로젝트 목적

- Slack 채널에서 **역할(persona)별 봇**이 기술 토론·기획·QA 등 관점을 나눠 응답하도록 한다.
- 사용자 명령으로 **일회성 회의/토론**, **소프트웨어 개발 파이프라인(설계→개발→QA) 시뮬레이션**, **정기 자동 회의**를 수행한다.
- 회의 **최종 결론을 로컬 JSON(`knowledge.json`)에 누적**해 이후 발제·토론 시 컨텍스트로 재사용한다.

### 해결하려는 문제

- 단일 챗봇 답변 대신 **PM / DEV / QA / ARCHITECT 등 역할 분담**으로 현실적인 토론 흐름을 만든다.
- Slack Events API의 **빠른 HTTP 200 응답 요구**(재시도 방지)와 **긴 LLM 처리 시간** 사이를 분리한다.

### 주요 기능 요약

| 구분 | 내용 |
|------|------|
| Slack 연동 | Events API 수신(`url_verification`, `event_callback`), Web API로 메시지 전송(`chat.postMessage`), 기동 시 `auth.test`로 봇 User ID ↔ persona 매핑 |
| 역할별 대화 | 채널별 메모리 히스토리(최대 10턴), persona별 시스템 프롬프트 |
| 명령 트리거 | `회의:` / `토론:` · `개발:` · `주기변경:` · `분야변경:` (일부는 PM persona에서만 오케스트레이션) |
| 정기 회의 | `BackgroundService`가 주기마다 PLANNER 발제 → 역할 순차 응답 → TEAM LEAD 결론 |
| 설정 API | Swagger 노출: 정기 회의 주기·발제 분야 조회/변경 |

---

## 2. 아키텍처 분석 (Architecture)

### 전체 구조 (Layer / Module)

```
slackbot/
├── Program.cs                 # DI, HostedService 등록, Slack 초기화
├── Controllers/
│   ├── SlackEventsController.cs   # Slack Events 수신 및 오케스트레이션 진입점
│   └── SettingsController.cs      # 회의 주기·분야 REST 설정
├── Services/
│   ├── AiTeamService.cs           # OpenAI 호출, 히스토리, knowledge.json
│   ├── SlackMessageService.cs    # Slack 토큰·메시지 전송·봇 ID 매핑
│   └── PeriodicMeetingService.cs # 정기 회의 백그라운드 루프
├── Models/
│   └── SlackPayload.cs           # Slack 이벤트 페이로드 모델
└── knowledge.json               # 과거 회의 결론 저장소(런타임 생성·갱신)
```

**역할**: 클래식 **Controller → Service** 분리. `AiTeamService`·`SlackMessageService`는 **Singleton**으로 채널 상태·HTTP 클라이언트 재사용.

### 데이터 흐름

```text
Slack Workspace
      │  HTTPS POST /api/slack/events
      ▼
SlackEventsController
      │  url_verification → challenge 원문 반환
      │  event_callback → 즉시 200, 본 처리는 비동기 큐(Task.Run)
      ▼
ProcessMessageEventAsync
      │  persona 판별(Slack authorizations.user_id → BotIdToPersona)
      │  명령 분기(회의/개발/설정/단순 멘션)
      ▼
AiTeamService ──HTTPS──► api.openai.com/v1/chat/completions
      │
SlackMessageService ──HTTPS──► slack.com/api/chat.postMessage
      ▼
Slack 채널 메시지
```

정기 회의:

```text
PeriodicMeetingService (BackgroundService)
      │  1초 간격 루프 + 경과 시간 ≥ MeetingIntervalSeconds 일 때 실행
      ▼
GeneratePlannerTopicAsync → 채널별 토론 순서(pm→dev→qa→architect) → GenerateConclusionAsync
      → knowledge.json 추가(결론 저장 시)
```

### 주요 컴포넌트 역할

| 컴포넌트 | 역할 |
|----------|------|
| `SlackEventsController` | Slack 검증·이벤트 수신, 무한 루프 방지(`BotId` 무시), 오케스트레이션 분기 |
| `AiTeamService` | GPT-4o 호출, 채널 히스토리, `knowledge.json` 읽기/쓰기, 회의 간격·발제 분야 상태 |
| `SlackMessageService` | 설정의 `SlackBots` 키별 토큰으로 `chat.postMessage`, `auth.test` 기반 persona 매핑 |
| `PeriodicMeetingService` | 고정 채널 + 활성 채널에 정기 시나리오 실행 |

---

## 3. 핵심 기술 스택 (Tech Stack)

| 항목 | 선택 |
|------|------|
| 언어 / 런타임 | C# 12, **.NET 8** (`net8.0`) |
| 웹 프레임워크 | ASP.NET Core (Minimal hosting + Controllers) |
| API 문서 | **Swashbuckle.AspNetCore** 6.6.2 |
| 외부 API | Slack Web API(REST), OpenAI Chat Completions(REST, `gpt-4o`) |
| 구성 | `appsettings.json` (`SlackBots`, `OpenAiApiKey`) |

### 선택 이유

- **ASP.NET Core**: 단일 프로세스에서 HTTP 수신·백그라운드 호스티드 서비스·Swagger를 묶기 적합.
- **직접 REST 호출(HttpClient)**: 의존성을 최소화하고 Slack/OpenAI 요청 페이로드를 코드에서 명시적으로 제어.
- **Singleton 서비스**: 채널별 히스토리와 봇 매핑을 요청 간 공유.

### 대안과 비교

| 대안 | 비교 |
|------|------|
| Slack Bolt SDK (.NET 미공식/다른 스택) | 공식 생태계는 주로 Node/Java/Python; 현재는 Raw REST로 충분하며 패키지 수를 줄임. |
| Azure OpenAI | 엔터프라이즈 규제·키 관리에 유리; 현재는 OpenAI 직접 엔드포인트 고정. |
| Redis/DB 히스토리 | 확장성·다중 인스턴스 일관성에 유리; 현재는 **인메모리 Dictionary**로 단일 프로세스 가정. |

---

## 4. 주요 구현 방식 (Implementation Details)

### Slack 이벤트: 빠른 응답 + 백그라운드 처리

Slack은 수 초 내 응답이 없으면 재시도하므로, 이벤트 본 처리는 `Task.Run`으로 분리한다.

```csharp
// SlackEventsController.cs — 이벤트 수신 후 즉시 OK
_ = Task.Run(() => ProcessMessageEventAsync(payload));
return Ok();
```

### persona 및 중복 실행 방지

- `payload.Authorizations`의 `user_id`를 `SlackMessageService.BotIdToPersona`와 매칭해 **어떤 봇으로 호출되었는지** 판별.
- `회의:`/`개발:` 등 오케스트레이션은 **`detectedPersona == "pm"`일 때만** 실행해 동일 채널 다중 봇 중복 실행을 줄인다.

### OpenAI 호출

- 모델: **`gpt-4o`**, 일반 토론 `temperature` 0.7, PLANNER 발제 0.9, 결론 0.5 등 시나리오별 상이.
- **개발 모드**(`isDevelopmentMode: true`): Architect → Dev → QA 순서에서 분량 제한 없는 산출물 프롬프트.

### 비동기 / 백그라운드

- HTTP: `async`/`await` 기반 `HttpClient` 호출.
- 정기 회의: `PeriodicMeetingService` : `BackgroundService`, 1초 단위 루프로 `MeetingIntervalSeconds` 변경에 반응.
- `Task.Run` 내에서 싱글톤 서비스 사용(스코프 없이) — 현재 설계와 일치.

### 통신 방식

- **HTTPS REST만 사용**(TCP/WebSocket 미사용).
- Slack: `auth.test`, `chat.postMessage`.
- OpenAI: `POST https://api.openai.com/v1/chat/completions`.

### 로컬 지식 베이스

- `AiTeamService.SaveConclusionToKnowledge`: 결론을 최대 **30건**까지 유지, 최근 **10건** 요약을 프롬프트에 주입.

### 기동 시 시퀀스

```csharp
// Program.cs
var slackService = app.Services.GetRequiredService<SlackMessageService>();
await slackService.InitializeBotIdsAsync();
app.Run();
```

### 실행·디버깅 URL

- `Properties/launchSettings.json`: 예) HTTPS `https://localhost:7074`, HTTP `http://localhost:5052`, Swagger 기본.
- 로컬에서 Slack이 접근하려면 **터널(예: ngrok)** 이 필요하며, `Program.cs` 주석에 `ngrok http https://localhost:7074` 안내가 있다.

---

## 5. 문제 해결 및 트러블슈팅 (Troubleshooting)

다음은 **코드베이스에서 확인되는 동작·운영 이슈**이다. 별도 이슈 트래커 기록은 **확인 필요**.

| 현상 | 원인 분석 | 해결·완화 |
|------|-----------|-----------|
| Slack이 같은 이벤트를 여러 번 전송 | Events API 타임아웃·느린 응답 | 본 처리 전 **즉시 200** 반환(현재 구현). 재시도 시 중복 처리 가능성은 **Idempotency 미구현** — 개선 여지 |
| 봇이 서로에게 무한 응답 | 봇 메시지도 이벤트로 옴 | `eventDetail.BotId` 있으면 무시 |
| 오케스트레이션이 여러 번 도는 느낌 | 동일 채널에 여러 앱/봇 | 회의/개발은 **PM persona에서만** 시작하도록 제한 |
| OpenAI 오류 문자열 반환 | API 키·쿼터·네트워크 | 로그 확인, 키·결제 상태 점검 |
| 정기 회의 채널이 곧바로 적용 안 됨 | 활성 채널은 `_chatHistory`에 한정 | 최소 한 번 대화가 있어야 목록에 포함될 수 있음 + **고정 채널 ID** 병합 |

**보안 (필수)**: 현재 형상의 `appsettings.json`에 Slack Bot Token·OpenAI 키가 들어갈 수 있다. 공개 저장소에는 **커밋하지 말 것**. 키 노출 시 **즉시 Slack에서 토큰 회전**, OpenAI 키 재발급 권장.

---

## 6. 개선 포인트 (Improvements)

| 영역 | 내용 |
|------|------|
| 구성 | 정기 회의용 **채널 ID 하드코딩**(`PeriodicMeetingService`) → 설정값으로 분리 필요 |
| 확장성 | 인메모리 히스토리 → 프로세스 재시작 시 소실; 다중 인스턴스 배포 시 **외부 저장소** 필요 |
| 신뢰성 | Slack `event_id` 기준 **중복 처리 방지**, `Task.Run` 예외 관측성(구조화 로깅·Application Insights 등) |
| HTTP 클라이언트 | 장수명 싱글톤과 `HttpClient` 권장 패턴(`IHttpClientFactory`) 검토 |
| 코드 품질 | `분야변경:` 조건 분기에 동일 문자열이 중복된 줄 있음 — 의도된지 **확인 필요** |

---

## 7. 배운 점 (Lessons Learned)

- Slack Events는 **응답 시간 계약**이 있어, LLM처럼 긴 작업은 HTTP 스레드에서 분리하는 패턴이 실무적으로 자주 쓰인다.
- **역할별 시스템 프롬프트 + 채널 히스토리**만으로도 “미니 멀티 에이전트” 같은 효과를 낼 수 있으나, 장기적으로는 상태 저장·중복 이벤트 처리 설계가 필요하다.
- 로컬 `knowledge.json`은 도입 비용은 낮지만 **동시 쓰기·배포 환경**에서는 파일 락·DB 이전을 고려해야 한다.

---

## 빌드 및 실행

```bash
dotnet restore
dotnet run --project slackbot/slackbot.csproj
```

- 개발 환경에서 Swagger UI 사용 (`Development`).
- Slack Request URL은 공개 HTTPS 엔드포인트 필요 → **ngrok 등 터널** 사용 후 `https://<host>/api/slack/events` 등록.

### 설정 키 (예시 형태만)

```json
{
  "SlackBots": {
    "default": "<xoxb-...>",
    "pm": "<xoxb-...>",
    "dev": "<xoxb-...>"
  },
  "OpenAiApiKey": "<OpenAI API Key>"
}
```

실제 값은 **User Secrets**, 환경 변수, 또는 비공개 설정 파일로 주입할 것.

---

## 솔루션

- 리포지토리 루트: `SlackAIBot.sln` → 프로젝트 `slackbot/slackbot.csproj` (솔루션 표시 이름과 폴더명이 다를 수 있음).

---

## 라이선스 / 기여

확인 필요 (저장소에 LICENSE 파일 없음).

---

## 참고 스크린샷 (기존 자료)

<details>
<summary>펼치기</summary>

이전 문서에서 사용하던 UI·동작 예시 이미지 링크이다.

- [Swagger](https://github.com/user-attachments/assets/77c02236-719b-4c3f-a296-fc5f6bcf1a3e)
- [회의](https://github.com/user-attachments/assets/3a2c472b-d0e1-4ea6-b748-7616cb9ca08f)
- [회의 주기 설정](https://github.com/user-attachments/assets/995e-44b0-b1fc-c2c95f1a99c6)
- [회의 분야 변경](https://github.com/user-attachments/assets/0b20890b-6b4a-4f04-ac7f-e6c9c2493cc4)
- [개발 시나리오](https://github.com/user-attachments/assets/c1b9bec0-8b35-4b1e-979a-975b9fbfecd4)
- [주제 요청](https://github.com/user-attachments/assets/5a0c01dc-5fbb-471a-9054-fa6bcc369404)

</details>
