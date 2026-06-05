using Microsoft.Extensions.Logging;
using SpeechTranslationPOC.Hubs;
using SpeechTranslationPOC.Models;
using SpeechTranslationPOC.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddAzureWebAppDiagnostics();
builder.Logging.SetMinimumLevel(LogLevel.Warning); // Default to Warning
builder.Logging.AddFilter("SpeechTranslationPOC", LogLevel.Information); // Our code: Information
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // Suppress hosting logs
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting", LogLevel.Warning);


builder.Services.AddRazorPages();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024;
    options.EnableDetailedErrors = true;
});

builder.Services.Configure<AzureSpeechOptions>(
    builder.Configuration.GetSection(AzureSpeechOptions.SectionName));
builder.Services.AddSingleton<AzureSpeechTokenProvider>();
builder.Services.AddSingleton<SpeechTranslationService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// Diagnostic endpoint -- hit /diag in browser to verify logging works
app.MapGet("/diag", (ILogger<Program> logger) =>
{
    var msg = $"[DIAG] App is running. Time: {DateTime.UtcNow}";
    logger.LogWarning(msg);
    Console.WriteLine(msg);
    Console.Error.WriteLine(msg);
    return Results.Ok(msg);
});

app.MapRazorPages();
app.MapHub<TranslationHub>("/translationHub");

app.Run();
