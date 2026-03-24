var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddSingleton<slackbot.Services.AiTeamService>();
builder.Services.AddSingleton<slackbot.Services.SlackMessageService>();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHostedService<slackbot.Services.PeriodicMeetingService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

var slackService = app.Services.GetRequiredService<slackbot.Services.SlackMessageService>();
await slackService.InitializeBotIdsAsync();

app.Run();
