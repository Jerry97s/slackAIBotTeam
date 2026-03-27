using Microsoft.AspNetCore.Mvc;
using slackbot.Models;
using slackbot.Services;

namespace slackbot.Controllers
{
    [ApiController]
    [Route("api/slack")]
    public class SlackEventsController : ControllerBase
    {
        private readonly AiTeamService _aiService;
        private readonly SlackMessageService _slackService;

        public SlackEventsController(AiTeamService aiService, SlackMessageService slackService)
        {
            _aiService = aiService;
            _slackService = slackService;
        }

        [HttpPost("events")]
        public IActionResult HandleEvent([FromBody] SlackEventPayload payload)
        {
            if (payload == null)
            {
                Console.WriteLine("Payload is null.");
                return BadRequest();
            }

            // 1. Handle URL Verification Challenge from Slack Configuration
            if (payload.Type == "url_verification" && payload.Challenge != null)
            {
                return Content(payload.Challenge, "text/plain");
            }

            // 2. Handle actual events
            if (payload.Type == "event_callback" && payload.Event != null)
            {
                // We must respond with 200 OK fast (within 3 seconds) to prevent Slack from retrying.
                var eventDetail = payload.Event;
                
                // Do not respond to our own bot's messages (prevent infinite loops)
                if (!string.IsNullOrEmpty(eventDetail.BotId))
                {
                     return Ok();
                }

                // Fire and forget so we don't block the request
                _ = Task.Run(() => ProcessMessageEventAsync(payload));

                return Ok();
            }

            return Ok();
        }

        private async Task ProcessMessageEventAsync(SlackEventPayload payload)
        {
            var eventDetail = payload.Event;
            if (eventDetail == null) return;
            try
            {
                if (eventDetail.Type != "message" && eventDetail.Type != "app_mention")
                    return;

                string text = eventDetail.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text))
                    return;

                // 글로벌 채널 ID (각 `if` 블록에서 중복 사용 방지)
                string channelId = eventDetail.Channel ?? "unknown-channel";

                // Remove the bot mention (e.g. <@U12345ABC> or similar)
                text = System.Text.RegularExpressions.Regex.Replace(text, @"<@[A-Z0-9]+>\s*", "").Trim();

                string detectedPersona = "dev"; // default
                using var authorizations = payload.Authorizations?.GetEnumerator();
                string? botUserId = payload.Authorizations?.FirstOrDefault()?.UserId;

                // 토큰 딕셔너리에서 현재 이벤트의 봇 User Id를 가지고 역할 지정
                if (!string.IsNullOrEmpty(botUserId) && _slackService.BotIdToPersona.TryGetValue(botUserId, out var mappedPersona))
                {
                    detectedPersona = mappedPersona;
                }

                // --- 1. 자동 오케스트레이션(토론/회의) 모드 ---
                if (text.StartsWith("회의:") || text.StartsWith("회의 :") || text.StartsWith("토론:") || text.StartsWith("토론 :"))
                {
                    if (detectedPersona != "pm") return; // 중복 실행 방지

                    string meetingTopic = text.Substring(text.IndexOf(":") + 1).Trim();
                    string sessionChannelId = eventDetail.Channel ?? "unknown-channel";

                    _aiService.ClearHistory(sessionChannelId);
                    _aiService.AddMessageToHistory(sessionChannelId, "User", meetingTopic);

                    string[] debateOrder = { "pm", "dev", "qa", "architect" };
                    foreach(var speaker in debateOrder)
                    {
                        if (!_slackService.BotIdToPersona.Values.Any(v => v.Equals(speaker, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        string currentHistory = _aiService.GetHistory(sessionChannelId);
                        string generatedResponse = await _aiService.GenerateResponseAsync(meetingTopic, speaker, currentHistory);
                        
                        _aiService.AddMessageToHistory(sessionChannelId, speaker, generatedResponse);
                        string finalMsg = $"*[{speaker.ToUpper()}]*\n{generatedResponse}";
                        
                        await _slackService.PostMessageAsync(sessionChannelId, finalMsg, speaker);
                        await Task.Delay(2000); 
                    }

                    string fullHistoryForLead = _aiService.GetHistory(sessionChannelId);
                    string conclusion = await _aiService.GenerateConclusionAsync(meetingTopic, fullHistoryForLead);
                    await _slackService.PostMessageAsync(sessionChannelId, $"*[TEAM LEAD (최종 결론)]*\n{conclusion}", "default");
                    return;
                }

                // --- 2. 자동 소프트웨어 개발 파이프라인 모드 ---
                if (text.StartsWith("개발:") || text.StartsWith("개발 :"))
                {
                    if (detectedPersona != "pm") return; // 중복 실행 방지

                    string devTopic = text.Substring(text.IndexOf(":") + 1).Trim();
                    string sessionChannelId = eventDetail.Channel ?? "unknown-channel";

                    _aiService.ClearHistory(sessionChannelId);
                    _aiService.AddMessageToHistory(sessionChannelId, "User (요구사항)", devTopic);

                    // 개발 파이프라인 순서: ARCHITECT(설계) -> DEV(구현) -> QA(테스트)
                    string[] devOrder = { "architect", "dev", "qa" };
                    foreach(var worker in devOrder)
                    {
                        if (!_slackService.BotIdToPersona.Values.Any(v => v.Equals(worker, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        string currentHistory = _aiService.GetHistory(sessionChannelId);
                        
                        // isDevelopmentMode 플래그를 true로 넘겨 분량 제한 해제 밑 실무 산출물 모드 활성화
                        string generatedResponse = await _aiService.GenerateResponseAsync(devTopic, worker, currentHistory, isDevelopmentMode: true);
                        
                        _aiService.AddMessageToHistory(sessionChannelId, worker, generatedResponse);
                        string finalMsg = $"*[{worker.ToUpper()} (작업 산출물)]*\n{generatedResponse}";
                        
                        await _slackService.PostMessageAsync(sessionChannelId, finalMsg, worker);
                        
                        // 코드를 짜야하므로 시간을 조금 더 길게 두어 OpenAI API 과부하 분산
                        await Task.Delay(4000); 
                    }

                    await _slackService.PostMessageAsync(sessionChannelId, "*[TEAM LEAD]*\n모든 개발 및 QA 절차가 완료되었습니다. 위 산출물과 테스트 케이스를 확인해 주세요.", "default");
                    return;
                }

                // --- 3. 설정 변경 명렁어 모드 ---
                if (text.StartsWith("주기변경:") || text.StartsWith("주기변경 :"))
                {
                    if (detectedPersona != "pm") return; // 중복 방지
                    string numStr = text.Substring(text.IndexOf(":") + 1).Trim();
                    
                    if (int.TryParse(numStr, out int seconds) && seconds >= 10)
                    {
                        _aiService.MeetingIntervalSeconds = seconds;
                        await _slackService.PostMessageAsync(channelId, $"*[SYSTEM]*\n타이머 설정 업데이트: 앞으로 정기 회의가 {seconds}초 주기로 실행됩니다.", "default");
                    }
                    else
                    {
                        await _slackService.PostMessageAsync(channelId, "*[SYSTEM]*\n오류: 주기변경 명령어 뒤에는 최소 10 이상의 숫자를 적어주세요. (예: `주기변경: 60`)", "default");
                    }
                    return;
                }

                if (text.StartsWith("분야변경:") || text.StartsWith("분야변경:"))
                {
                    if (detectedPersona != "pm") return; // 중복 방지
                    string newField = text.Substring(text.IndexOf(":") + 1).Trim();

                    if (!string.IsNullOrEmpty(newField))
                    {
                        _aiService.MeetingTopicField = newField;
                        await _slackService.PostMessageAsync(channelId, $"*[SYSTEM]*\n정기 회의 토론 주제 분야가 업데이트되었습니다.\n앞으로 발제자는 '{newField}' 에 관련된 주제로 회의를 엽니다.", "default");
                    }
                    return;
                }
                
                // --- 4. 봇 자신을 직접 호출했는지 일반 대화 검사 (예: "dev 알려줘", "dev님", "dev:") ---
                string pattern = $@"^{detectedPersona}(님|야)?([\s,:]+|$)";
                if (!System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // 자신을 부른 게 아니면 무시
                    return;
                }

                // 호출 명령어 부분을 제거하고 순수 내용만 추출
                string userPrompt = System.Text.RegularExpressions.Regex.Replace(
                    text, 
                    pattern, 
                    "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase
                ).Trim();

                // 히스토리에 방금 들어온 최신 유저 메시지 먼저 추가 (" User" 라는 발신자로)
                _aiService.AddMessageToHistory(channelId, "User", userPrompt);

                // 현재 채널의 누적 대화 내역 가져오기
                string channelHistory = _aiService.GetHistory(channelId);

                // Send the persona system prompt and user query to OpenAI (히스토리 포함)
                string aiResponse = await _aiService.GenerateResponseAsync(userPrompt, detectedPersona, channelHistory);

                // 봇의 답변을 다시 히스토리에 추가
                _aiService.AddMessageToHistory(channelId, detectedPersona, aiResponse);

                // Add Persona prefix to message
                string finalResponse = $"*[{detectedPersona.ToUpper()}]*\n{aiResponse}";

                await _slackService.PostMessageAsync(channelId, finalResponse, detectedPersona);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
            }
        }
    }
}
