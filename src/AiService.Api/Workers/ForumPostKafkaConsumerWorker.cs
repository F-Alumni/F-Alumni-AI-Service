using System.Text.Json;
using Confluent.Kafka;
using Contracts.Events;
using AiService.Services.Interfaces;

namespace AiService.Api.Workers;

public class ForumPostKafkaConsumerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ForumPostKafkaConsumerWorker> _logger;

    public ForumPostKafkaConsumerWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<ForumPostKafkaConsumerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"]
            ?? Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS");
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogInformation("Kafka:BootstrapServers chưa được cấu hình. ForumPostKafkaConsumerWorker ở trạng thái chờ.");
            return;
        }

        var topic = _configuration["Kafka:Topics:PostCreated"] ?? PostCreatedEventV1.DefaultTopic;
        var groupId = _configuration["Kafka:ConsumerGroupId"] ?? "ai-service-rag-consumer";

        try
        {
            await Task.Delay(2000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        try
        {
            using var consumer = new ConsumerBuilder<Ignore, string>(config).Build();
            consumer.Subscribe(topic);
            _logger.LogInformation("ForumPostKafkaConsumerWorker đã đăng ký lắng nghe topic Kafka: {Topic}", topic);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = await Task.Run(
                        () => consumer.Consume(TimeSpan.FromMilliseconds(500)),
                        stoppingToken);
                    if (consumeResult == null || string.IsNullOrWhiteSpace(consumeResult.Message?.Value))
                    {
                        await Task.Delay(100, stoppingToken);
                        continue;
                    }

                    var envelope = JsonSerializer.Deserialize<EventEnvelope<PostCreatedEventV1>>(
                        consumeResult.Message.Value,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (envelope?.Data != null)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        var ingestion = scope.ServiceProvider.GetRequiredService<IRagIngestionService>();
                        await ingestion.IngestForumPostAsync(
                            envelope.Data.PostId,
                            envelope.Data.Title,
                            envelope.Data.Content,
                            envelope.Data.AuthorName,
                            envelope.Data.Major,
                            envelope.Data.Campus,
                            stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("Lỗi tiêu thụ thông điệp Kafka ({Reason}). Đang thử lại...", ex.Error.Reason);
                    await Task.Delay(5000, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi xử lý sự kiện post.created.v1 từ Kafka.");
                    try
                    {
                        await Task.Delay(2000, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            consumer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không thể khởi tạo Kafka Consumer (broker có thể chưa chạy). Dịch vụ AI vẫn hoạt động bình thường.");
        }
    }
}
