using SpeechTranslationPOC.Hubs;
using SpeechTranslationPOC.Models;
using SpeechTranslationPOC.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB for audio chunks
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.Configure<AzureSpeechOptions>(
    builder.Configuration.GetSection(AzureSpeechOptions.SectionName));
builder.Services.AddSingleton<AzureSpeechTokenProvider>();
builder.Services.AddSingleton<SpeechTranslationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();
app.MapHub<TranslationHub>("/translationHub");

app.Run();
