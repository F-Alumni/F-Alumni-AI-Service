using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AiService.Repositories.Interfaces;
using AiService.Services.Interfaces;

namespace AiService.Api.Workers;

public class RagStartupWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RagStartupWorker> _logger;
    private readonly IConfiguration _configuration;

    public RagStartupWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<RagStartupWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait 1 second after startup
        await Task.Delay(1000, stoppingToken);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var ragRepo = scope.ServiceProvider.GetRequiredService<IRagRepository>();
            var count = await ragRepo.GetChunkCountAsync(stoppingToken);

            var ragDir = _configuration["Rag:DefaultDirectory"] ?? @"C:\Users\vinhmt\Downloads\RAG";
            if (count == 0 && Directory.Exists(ragDir))
            {
                _logger.LogInformation("Cơ sở dữ liệu tri thức RAG đang trống. Bắt đầu tự động nạp từ: {Dir}", ragDir);
                var ingestionService = scope.ServiceProvider.GetRequiredService<IRagIngestionService>();
                var res = await ingestionService.IngestDocumentsFromDirectoryAsync(ragDir, forceReindex: false, stoppingToken);
                _logger.LogInformation("Tự động nạp hoàn tất: {Msg}", res.Message);
            }
            else
            {
                _logger.LogInformation("Hệ thống RAG đã có sẵn {Count} đoạn tri thức.", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Không thể tự động nạp dữ liệu RAG khi khởi động: {Message}", ex.Message);
        }
    }
}
