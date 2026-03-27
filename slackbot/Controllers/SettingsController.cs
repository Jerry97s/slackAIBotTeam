using Microsoft.AspNetCore.Mvc;
using slackbot.Services;

namespace slackbot.Controllers
{
    [ApiController]
    [Route("api/settings")]
    public class SettingsController : ControllerBase
    {
        private readonly AiTeamService _aiService;

        public SettingsController(AiTeamService aiService)
        {
            _aiService = aiService;
        }

        /// <summary>
        /// 현재 정기 회의 타이머 주기 및 발제 분야 설정을 조회합니다.
        /// </summary>
        [HttpGet]
        public IActionResult GetCurrentSettings()
        {
            return Ok(new 
            {
                MeetingIntervalSeconds = _aiService.MeetingIntervalSeconds,
                MeetingTopicField = _aiService.MeetingTopicField
            });
        }

        public class UpdateSettingsRequest
        {
            /// <summary>
            /// 회의 타이머 주기 (단위: 초). 10초 이상의 숫자만 가능합니다.
            /// </summary>
            public int? MeetingIntervalSeconds { get; set; }

            /// <summary>
            /// 발제할 토론 주제 분야 (예: '프론트엔드 최신 아키텍처')
            /// </summary>
            public string? MeetingTopicField { get; set; }
        }

        /// <summary>
        /// 정기 회의 타이머 주기 또는 발제 분야를 수정합니다.
        /// </summary>
        [HttpPost]
        public IActionResult UpdateSettings([FromBody] UpdateSettingsRequest request)
        {
            if (request.MeetingIntervalSeconds.HasValue)
            {
                if (request.MeetingIntervalSeconds.Value < 10)
                {
                    return BadRequest("타이머 주기는 최소 10초 이상이어야 합니다.");
                }
                _aiService.MeetingIntervalSeconds = request.MeetingIntervalSeconds.Value;
            }

            if (!string.IsNullOrWhiteSpace(request.MeetingTopicField))
            {
                _aiService.MeetingTopicField = request.MeetingTopicField;
            }

            return Ok(new 
            { 
                Message = "성공적으로 설정이 업데이트 되었습니다.",
                MeetingIntervalSeconds = _aiService.MeetingIntervalSeconds,
                MeetingTopicField = _aiService.MeetingTopicField
            });
        }
    }
}
