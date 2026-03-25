using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace slackbot.Services
{
    public class PeriodicMeetingService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public PeriodicMeetingService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 1시간 주기로 대기합니다. 
                // (테스트를 원하시면 TimeSpan.FromSeconds(60) 처럼 시간을 줄여서 확인해 보세요)
                //await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var slackService = scope.ServiceProvider.GetRequiredService<SlackMessageService>();
                    var aiService = scope.ServiceProvider.GetRequiredService<AiTeamService>();

                    // 사용자가 지정한 고정 채널 ID (정기 회의방)
                    string fixedChannelId = "슬랙채널ID";
                    
                    // 봇이 한 번이라도 대화/관찰했던 활성 채널 + 고정 채널을 합칩니다.
                    var channels = aiService.GetActiveChannels().ToList();
                    if (!channels.Contains(fixedChannelId))
                    {
                        channels.Add(fixedChannelId);
                    }

                    if (!channels.Any()) continue;  

                    foreach (var channelId in channels)
                    {
                        // 1. Planner 발제 (랜덤 주제 생성)
                        string plannerTopic = await aiService.GeneratePlannerTopicAsync();
                        string announceMsg = $"*🔔 [정기 발제] PLANNER*\n{plannerTopic}";
                        
                        // Planner 토큰이 설정되어 있으면 해당 이름으로, 아니면 기본(default)으로 보냄
                        string plannerTokenActor = slackService.BotIdToPersona.Values.Any(v => v.Equals("planner", StringComparison.OrdinalIgnoreCase)) ? "planner" : "default";
                        await slackService.PostMessageAsync(channelId, announceMsg, plannerTokenActor);
                        
                        // 2. 새로운 정기 회의이므로 해당 채널의 히스토리 초기화 후 발제 제안 넣기
                        aiService.ClearHistory(channelId);
                        aiService.AddMessageToHistory(channelId, "PLANNER", plannerTopic);
                        
                        // 3. 토론 연결 (Planner의 의제를 바탕으로)
                        string[] debateOrder = { "pm", "dev", "qa", "architect" };
                        foreach(var speaker in debateOrder)
                        {
                            if (!slackService.BotIdToPersona.Values.Any(v => v.Equals(speaker, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            string currentHistory = aiService.GetHistory(channelId);
                            string generatedResponse = await aiService.GenerateResponseAsync(plannerTopic, speaker, currentHistory);
                            
                            aiService.AddMessageToHistory(channelId, speaker, generatedResponse);
                            string finalMsg = $"*[{speaker.ToUpper()}]*\n{generatedResponse}";
                            
                            await slackService.PostMessageAsync(channelId, finalMsg, speaker);
                            await Task.Delay(3000, stoppingToken); 
                        }

                        // 4. 최종 결론
                        string fullHistory = aiService.GetHistory(channelId);
                        string conclusion = await aiService.GenerateConclusionAsync(plannerTopic, fullHistory);
                        await slackService.PostMessageAsync(channelId, $"*[TEAM LEAD (정기 회의 종합 결론)]*\n{conclusion}", "default");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PeriodicMeetingService Error: {ex.Message}");
                }
            }
        }
    }
}
