using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace slackbot.Models
{
    public class SlackEventPayload
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("challenge")]
        public string? Challenge { get; set; }

        [JsonPropertyName("event")]
        public SlackEventDetail? Event { get; set; }

        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("api_app_id")]
        public string? ApiAppId { get; set; }

        [JsonPropertyName("authorizations")]
        public List<SlackAuthorization>? Authorizations { get; set; }
    }

    public class SlackAuthorization
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }
    }

    public class SlackEventDetail
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("user")]
        public string? User { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("bot_id")]
        public string? BotId { get; set; }

        [JsonPropertyName("client_msg_id")]
        public string? ClientMsgId { get; set; }
    }
}
