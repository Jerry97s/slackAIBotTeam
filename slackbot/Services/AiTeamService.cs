using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System;

namespace slackbot.Services
{
    public class AiTeamService
    {
        private readonly HttpClient _httpClient;
        private readonly string? _openAiApiKey;
        
        // 메모리 기반의 간단한 채널별 대화 기록(히스토리) 저장소
        private readonly Dictionary<string, List<string>> _chatHistory = new();

        // 정기 회의 동적 설정 상태
        public int MeetingIntervalSeconds { get; set; } = 3600; // 기본 1시간
        public string MeetingTopicField { get; set; } = "실무적인 SW/IT 프로젝트"; // 기본 토론 분야

        public AiTeamService(IConfiguration configuration)
        {
            _httpClient = new HttpClient();
            _openAiApiKey = configuration["OpenAiApiKey"];
        }

        public void AddMessageToHistory(string channel, string persona, string message)
        {
            if (!_chatHistory.ContainsKey(channel))
                _chatHistory[channel] = new List<string>();

            string prefix = string.IsNullOrEmpty(persona) ? "User" : persona.ToUpper();
            _chatHistory[channel].Add($"[{prefix}]: {message}");

            // 너무 길어지면 토큰을 많이 소모하므로 최근 10개로 제한
            if (_chatHistory[channel].Count > 10)
                _chatHistory[channel].RemoveAt(0);
        }

        private static readonly string KnowledgeFilePath = "knowledge.json";

        public void ClearHistory(string channel)
        {
            if (_chatHistory.ContainsKey(channel))
                _chatHistory[channel].Clear();
        }
        
        public void SaveConclusionToKnowledge(string topic, string conclusion)
        {
            try 
            {
                var knowledgeList = new List<Dictionary<string, string>>();
                if (File.Exists(KnowledgeFilePath))
                {
                    var content = File.ReadAllText(KnowledgeFilePath);
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        knowledgeList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(content) ?? new List<Dictionary<string, string>>();
                    }
                }

                var newEntry = new Dictionary<string, string>
                {
                    { "Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm") },
                    { "Topic", topic },
                    { "Conclusion", conclusion }
                };

                knowledgeList.Add(newEntry);
                if (knowledgeList.Count > 30) knowledgeList.RemoveAt(0);

                File.WriteAllText(KnowledgeFilePath, JsonSerializer.Serialize(knowledgeList, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            } 
            catch (Exception ex) 
            {
                Console.WriteLine($"[Knowledge] Save Error: {ex.Message}");
            }
        }

        public string GetKnowledgeBase()
        {
            if (!File.Exists(KnowledgeFilePath)) return "저장된 과거 지식 없음.";
            try
            {
                var content = File.ReadAllText(KnowledgeFilePath);
                var knowledgeList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(content);
                if (knowledgeList == null || knowledgeList.Count == 0) return "저장된 과거 지식 없음.";

                var sb = new StringBuilder();
                var recentKnowledge = knowledgeList.TakeLast(10);
                foreach (var item in recentKnowledge)
                {
                    sb.AppendLine($"- [과거 논의 주제]: {item.GetValueOrDefault("Topic")}");
                    string conc = item.GetValueOrDefault("Conclusion") ?? "";
                    if (conc.Length > 150) conc = conc.Substring(0, 150) + "..."; // 프롬프트 길이를 위해 결론 요약
                    sb.AppendLine($"  [당시 팀 결론 요약]: {conc}");
                }
                return sb.ToString();
            }
            catch 
            { 
                return "과거 지식 로드 실패."; 
            }
        }

        public IEnumerable<string> GetActiveChannels()
        {
            return _chatHistory.Keys;
        }

        public async Task<string> GeneratePlannerTopicAsync()
        {
            if (string.IsNullOrEmpty(_openAiApiKey)) return "API Key 오류";
            
            string knowledge = GetKnowledgeBase();
            string systemPrompt = $@"당신은 IT 회사의 핵심 서비스 기획자(PLANNER)입니다.
팀원들이 열띠게 토론할 만한 '{MeetingTopicField}' 관련 토론 주제 1가지를 새롭게 발제하세요.

[과거에 우리 팀이 이미 논의했던 주제와 결론들]
{knowledge}

- 과거에 논의했던 주제와 절대 중복되지 않는 완전히 새로운 주제를 제시하세요.
- 과거의 결론에서 파생된 심화 안건이어도 좋습니다.
- 인사말 없이 발제 내용만 곧바로 말하세요.
- 최대한 구체적이고 현실적인 요구사항이나 안건을 제시하세요.
- 반드시 한국어(Korean)로 작성하세요.
- 분량은 2~3문장 이내로 작성하세요.";

            var requestBody = new
            {
                model = "gpt-4o",
                messages = new[] 
                { 
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = "새롭고 구체적인 랜덤 IT 실무 토론 주제 하나를 즉시 던져주세요." }
                },
                temperature = 0.9 // 창의성을 높여 매번 다른 주제가 나오도록 향상
            };
            
            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = jsonContent,
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey) }
            };

            try 
            {
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "주제 생성 실패";
                }
                else
                {
                    var errorStr = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[GeneratePlanner] API Error: {response.StatusCode} - {errorStr}");
                }
            } 
            catch (Exception ex)
            {
                Console.WriteLine($"[GeneratePlanner] Exception: {ex.Message}");
            }
            return "오늘의 긴급 토론: 사용자 인증 성능을 100% 높이려면 어떤 인프라 설계와 캐싱 전략이 필요할까요?";
        }

        public string GetHistory(string channel)
        {
            if (!_chatHistory.ContainsKey(channel) || _chatHistory[channel].Count == 0)
                return "이전 대화 없음";

            return string.Join("\n", _chatHistory[channel]);
        }

        public async Task<string> GenerateResponseAsync(string textToProcess, string persona, string history, bool isDevelopmentMode = false)
        {
            if (string.IsNullOrEmpty(_openAiApiKey))
            {
                return "Error: OpenAiApiKey is not configured in appsettings.json. Please configure it to use the AI team.";
            }

            var systemPrompt = GetSystemPromptForPersona(persona, isDevelopmentMode);
            string knowledge = GetKnowledgeBase();

            string userMessage = isDevelopmentMode ? $@"[요구사항/주제]
{textToProcess}

[지금까지의 작업 산출물]
{history}

[지시]
- 앞선 산출물을 이어받아 당신의 역할에 맞는 실무 작업(설계, 코드 작성, 테스트 기획/리뷰 등)을 수행하세요.
- 구체적이고 전문적인 결과물을 분량 제한 없이 상세하게 출력하세요." 
            : $@"[주제]
{textToProcess}

[우리 팀의 과거 지식 베이스 (참조 바람)]
{knowledge}

[현재 채널의 토론 대화 내용]
{history}

[지시]
- 이전 의견들을 참고해서 이어서 발언하세요.
- 과거 지식 베이스를 바탕으로 우리 팀이 이미 내렸던 결정이라면 이를 근거로 활용하거나 발전시키세요.
- 필요하면 반박하거나 보완하세요.
- 새로운 내용 위주로 말하세요.
- 짧고 명확하게 말하세요.";

            var requestBody = new
            {
                model = "gpt-4o",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                temperature = 0.7
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = jsonContent,
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey) }
            };

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return $"AI API Error: {response.StatusCode} - {errorContent}";
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var jsonDocument = JsonDocument.Parse(responseJson);
            var resultText = jsonDocument.RootElement
                                         .GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString();

            return resultText ?? "No response from AI.";
        }

        public async Task<string> GenerateConclusionAsync(string topic, string history)
        {
            if (string.IsNullOrEmpty(_openAiApiKey))
                return "Error: OpenAiApiKey is not configured.";
            
            string systemPrompt = @"당신은 팀 리드입니다.

[목표]
아래 토론 내용을 기반으로 최종 결론을 만들어라.

[규칙]
- 모든 내용은 반드시 한국어(Korean)로 작성하세요.
- 중복 제거
- 핵심만 정리
- 실행 가능한 액션 제시
- 너무 길지 않게";

            string userMessage = $@"[주제]
{topic}

[토론 내용]
{history}";

            var requestBody = new
            {
                model = "gpt-4o",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userMessage }
                },
                temperature = 0.5
            };
            
            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
            {
                Content = jsonContent,
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _openAiApiKey) }
            };

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return $"AI API Error: {response.StatusCode} - {errorContent}";
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var jsonDocument = JsonDocument.Parse(responseJson);
            var resultText = jsonDocument.RootElement
                                         .GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString();

            string finalConclusion = resultText ?? "No conclusion from AI.";
            
            // 시스템 성공 결론일 경우 로컬 Json 파일(지식 베이스)에 저장하여 다음 토론의 밑거름으로 씀
            if (!finalConclusion.Contains("API Error") && !finalConclusion.Contains("No conclusion"))
            {
                SaveConclusionToKnowledge(topic, finalConclusion);
            }

            return finalConclusion;
        }

        private string GetSystemPromptForPersona(string persona, bool isDevelopmentMode)
        {
            if (isDevelopmentMode)
            {
                string commonDevPrompt = @"당신은 실제 소프트웨어를 개발하고 출시하기 위한 SW 개발 팀의 전문가입니다.

[절대 규칙]
- 모든 대화와 산출물 설명은 반드시 한국어(Korean)로 작성하세요. (단, 프로그래밍 코드 자체는 예외)
- 토론이나 추상적인 대화를 하지 마세요. 구체적이고 실무적인 산출물(코드, 아키텍처 다이어그램, 테스트 케이스 문서 등)을 직접 작성하세요.
- 자신의 역할(설계, 개발, QA)에만 완전히 몰입하세요.
- 앞선 팀원이 만든 결과물을 바탕으로 이어서 작업을 진행하여 최종 완성도를 높이세요.
- 코드나 문서의 분량 제한은 없습니다. 최대한 전문적으로 작성하세요.";

                string roleDevPrompt = persona.ToLower() switch
                {
                    "architect" => "[ARCHITECT] 요구사항을 분석하고, 필요한 기술 스택 명세, 시스템 구조(아키텍처), 클래스 및 모듈 설계를 마크다운을 활용해 매우 상세하게 작성하세요.",
                    "dev" => "[DEV] 앞서 작성된 설계나 요구사항을 완벽히 이해하고, 복붙해서 실행 가능할 수준의 구체적이고 클린한 코드를 작성하세요. 주석 및 예외 처리 로직도 포함해야 합니다.",
                    "qa" => "[QA] 작성된 코드와 요구사항을 바탕으로 리스크 엣지 케이스를 찾아내고, 검증 가능한 테스트 시나리오 또는 단위 테스트(Unit Test) 코드를 작성하세요.",
                    _ => "[일반 팀원] 본인의 전문적인 시각에서 실무 산출물을 작성하세요."
                };

                return commonDevPrompt + "\n\n" + roleDevPrompt;
            }

            string commonPrompt = @"공통 : 
당신은 여러 역할이 참여하는 토론 시스템의 구성원입니다.

[절대 규칙]
- 모든 문장은 반드시 한국어(Korean)로 작성하세요.
- 반드시 자신의 역할 관점에서만 말하세요.
- 다른 역할의 의견에 대해 동의, 반박, 보완이 가능합니다.
- 최대 5문장 이내로 짧게 말하세요.
- 이미 나온 내용을 반복하지 마세요.
- 새로운 인사이트를 추가하세요.

[토론 방식]
- 요약하지 말고 ""의견""을 말하세요.
- 필요하면 다른 역할을 비판해도 됩니다.
- 현실적인 내용만 말하세요.

[금지]
- 최종 결론을 내리지 마세요.
- 두루뭉술한 말 금지";

            string rolePrompt = persona.ToLower() switch
            {
                "dev" => @"DEV (개발자)
당신은 시니어 개발자입니다.

[집중할 것]
- 기술적 원인
- 구현 방법
- 시스템 구조
- 성능/확장성

[해야 할 것]
- 기술적으로 말이 안 되는 부분 지적
- 구체적인 해결 방법 제시
- 리스크 분석

[금지]
- 비즈니스/기획 이야기
- 추상적인 말",

                "qa" => @"QA (테스터)
당신은 QA 엔지니어입니다.

[집중할 것]
- 테스트 케이스
- 예외 상황
- 재현 가능성
- 실패 시나리오

[해야 할 것]
- ""이거 깨질 수 있는데?"" 관점
- 빠진 케이스 지적
- 검증 방법 제시

[금지]
- 구현 방법 설명
- 애매한 말",

                "pm" => @"PM (기획자)
당신은 PM입니다.

[집중할 것]
- 사용자 영향
- 우선순위
- 비용 대비 효과
- 실제 필요한 기능인지

[해야 할 것]
- 과한 개발 제동
- ""이거 꼭 필요함?"" 질문
- 현실적인 방향 제시

[금지]
- 기술 구현 설명",

                "architect" => @"ARCHITECT (설계자)
당신은 시스템 아키텍트입니다.

[집중할 것]
- 전체 구조
- 모듈 분리
- 확장성
- 유지보수

[해야 할 것]
- DEV vs PM 의견 중재
- 구조적인 해결책 제시
- 장기 관점 판단

[금지]
- 코드 레벨 상세 구현",

                "analyze" => "[ANALYZE (데이터 분석가)] 성능 최적화, 시간/공간 복잡도, 데이터 기반의 통계적 타당성에 집중하세요.",
                "doc" => "[DOC (문서화 전문가)] 정보의 명확성, 가독성, 사용성, API 명세화에 집중하세요.",
                "ops" => "[OPS (인프라 전문가)] 배포 전략, CI/CD, 인프라 비용, 보안, 확장성에 집중하세요.",
                "planner" => "[PLANNER (아이디어 기획자)] UX 혁신, 창의적인 시나리오 추가, 시장 트렌드에 집중하세요.",
                _ => "[일반 팀원] 논리적인 시각으로 짧고 명확하게 의견을 제시하세요."
            };

            return commonPrompt + "\n\n" + rolePrompt;
        }
    }
}
