using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace slackbot.Services
{
    public class SlackMessageService
    {
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _slackBots;
        
        // 봇의 UserId 매핑 저장 (예: "U12345" -> "dev")
        public Dictionary<string, string> BotIdToPersona { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SlackMessageService(IConfiguration configuration)
        {
            _httpClient = new HttpClient();
            _slackBots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var botsSection = configuration.GetSection("SlackBots");
            if (botsSection.Exists())
            {
                foreach (var child in botsSection.GetChildren())
                {
                    _slackBots[child.Key] = child.Value ?? string.Empty;
                }
            }
        }

        public async Task InitializeBotIdsAsync()
        {
            foreach (var kvp in _slackBots)
            {
                var persona = kvp.Key;
                var token = kvp.Value;
                if (string.IsNullOrEmpty(token)) continue;

                var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test")
                {
                    Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
                };

                try
                {
                    var response = await _httpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.GetProperty("ok").GetBoolean())
                        {
                            if (doc.RootElement.TryGetProperty("user_id", out var userIdElement))
                            {
                                var userId = userIdElement.GetString();
                                if (!string.IsNullOrEmpty(userId))
                                {
                                    BotIdToPersona[userId] = persona;
                                    Console.WriteLine($"[Init] Mapped Slack Bot User ID {userId} to persona '{persona}'");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"[Init] Failed to get Auth info for '{persona}'. API returned ok: false.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Init] Error initializing {persona}: {ex.Message}");
                }
            }
        }

        public async Task PostMessageAsync(string channel, string text, string persona = "default")
        {
            if (!_slackBots.TryGetValue(persona, out var token) && !_slackBots.TryGetValue("default", out token))
            {
                Console.WriteLine($"Error: No Slack token found for persona '{persona}' and no 'default' token provided in appsettings.json.");
                return;
            }

            if (string.IsNullOrEmpty(token))
            {
                Console.WriteLine($"Error: Slack token for '{persona}' is empty.");
                return;
            }

            var requestBody = new
            {
                channel = channel,
                text = text
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage")
            {
                Content = jsonContent,
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
            };

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Slack API Error: {response.StatusCode} - {errorContent}");
            }
        }
    }
}
