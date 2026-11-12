using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AiService.Repositories.Entities;
using AiService.Repositories.Interfaces;
using AiService.Services.DTOs.Requests;
using AiService.Services.DTOs.Responses;
using AiService.Services.Exceptions;
using AiService.Services.Interfaces;

namespace AiService.Services.Implementations;

public class AiChatService : IAiChatService
{
    private readonly IRagRepository _ragRepository;
    private readonly IRagSearchService _ragSearchService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiChatService> _logger;
    private readonly string? _geminiApiKey;
    private readonly string _geminiModel;

    public AiChatService(
        IRagRepository ragRepository,
        IRagSearchService ragSearchService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AiChatService> logger)
    {
        _ragRepository = ragRepository;
        _ragSearchService = ragSearchService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _geminiApiKey = configuration["Gemini:ApiKey"]
            ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        _geminiModel = configuration["Gemini:Model"] ?? "gemini-1.5-flash";
    }

    public async Task<AiChatResponse> AskAsync(AiChatRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        // 1. Get or create conversation
        AiConversation? conversation = null;
        if (request.ConversationId.HasValue)
        {
            conversation = await _ragRepository.GetConversationAsync(request.ConversationId.Value, userId, cancellationToken);
        }

        if (conversation == null)
        {
            var title = request.Question.Length > 50 ? request.Question[..50] + "..." : request.Question;
            conversation = await _ragRepository.CreateConversationAsync(new AiConversation
            {
                UserId = userId,
                Title = title,
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow
            }, cancellationToken);
        }

        // Multi-turn context resolution: Inherit major & topic if follow-up question is anaphoric
        var resolvedMajor = request.Major;
        var enrichedQuery = request.Question;

        if (string.IsNullOrWhiteSpace(resolvedMajor) || resolvedMajor.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            var currentInferred = InferMajorFromText(request.Question);
            if (!string.IsNullOrEmpty(currentInferred))
            {
                resolvedMajor = currentInferred;
            }
        }

        if (conversation.Messages.Count > 0)
        {
            var lastUserMsg = conversation.Messages.OrderBy(m => m.CreatedAt).LastOrDefault(m => m.Role == "user");
            if (lastUserMsg != null)
            {
                if (string.IsNullOrWhiteSpace(resolvedMajor) || resolvedMajor.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    var inferred = InferMajorFromText(lastUserMsg.Content);
                    if (!string.IsNullOrEmpty(inferred))
                    {
                        resolvedMajor = inferred;
                    }
                }

                // If follow-up question refers back to previous topic without its own primary topic
                var qFlat = RagSearchService.RemoveDiacritics(request.Question.ToLowerInvariant());
                bool hasOwnSpecificTopic = qFlat.Contains("hoc phi") || qFlat.Contains("ky tuc") || qFlat.Contains("ktx") || qFlat.Contains("vovinam") || qFlat.Contains("coursera") || qFlat.Contains("fap");
                if (!hasOwnSpecificTopic && (
                    qFlat.Contains("nganh nay") || qFlat.Contains("nganh do") ||
                    qFlat.Contains("mon nay") || qFlat.Contains("mon do") ||
                    qFlat.Contains("the con") || qFlat.Contains("vay con") ||
                    qFlat.Contains("o dau") || qFlat.Contains("may tin") ||
                    qFlat.Contains("bao nhieu tin") || qFlat.Contains("thi sao") ||
                    qFlat.Contains("cu the") || qFlat.Contains("chi tiet") ||
                    qFlat.Contains("dieu kien") || qFlat.Contains("hoc phi cua no")))
                {
                    enrichedQuery = $"{request.Question} {lastUserMsg.Content}";
                }
            }
        }

        // 2. Retrieve relevant context chunks via RAG engine
        var relevantChunks = await _ragSearchService.SearchRelevantChunksAsync(
            enrichedQuery,
            resolvedMajor,
            request.Campus,
            topK: 4,
            cancellationToken);

        // 3. Build citations list
        var citations = relevantChunks.Select(rc => new CitationResponse
        {
            SourceTitle = rc.Chunk.Document?.Title ?? "Tài liệu FPTU",
            Section = rc.Chunk.Section,
            ProgramCode = rc.Chunk.Document?.ProgramCode,
            Snippet = GetCleanSnippet(rc.Chunk.Content, 250)
        }).ToList();

        // 4. Generate answer: Gemini if configured, otherwise high-accuracy local synthesis
        string answer;
        if (!string.IsNullOrWhiteSpace(_geminiApiKey) && relevantChunks.Count > 0)
        {
            answer = await GenerateWithGeminiAsync(request.Question, relevantChunks, conversation.Messages, cancellationToken);
        }
        else
        {
            answer = GenerateLocalGroundedAnswer(request.Question, relevantChunks);
        }

        // 5. Persist messages in conversation history
        var userMsg = new AiMessage
        {
            ConversationId = conversation.Id,
            Role = "user",
            Content = request.Question,
            CreatedAt = DateTime.UtcNow
        };
        await _ragRepository.AddMessageAsync(userMsg, cancellationToken);

        var assistantMsg = new AiMessage
        {
            ConversationId = conversation.Id,
            Role = "assistant",
            Content = answer,
            CitationsJson = JsonSerializer.Serialize(citations),
            CreatedAt = DateTime.UtcNow
        };
        await _ragRepository.AddMessageAsync(assistantMsg, cancellationToken);

        return new AiChatResponse
        {
            ConversationId = conversation.Id,
            Answer = answer,
            Citations = citations,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string? InferMajorFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var flat = RagSearchService.RemoveDiacritics(text).ToLowerInvariant();
        if (Regex.IsMatch(flat, @"\b(bit_se|se|ktpm)\b") || flat.Contains("phan mem") || flat.Contains("software")) return "BIT_SE";
        if (Regex.IsMatch(flat, @"\b(bit_ai)\b") || Regex.IsMatch(text, @"\bAI\b") || flat.Contains("tri tue nhan tao") || flat.Contains("nganh ai") || flat.Contains("chuyen nganh ai")) return "BIT_AI";
        if (Regex.IsMatch(flat, @"\b(bit_ia|ia|attt)\b") || flat.Contains("an toan thong tin") || flat.Contains("bao mat")) return "BIT_IA";
        if (Regex.IsMatch(flat, @"\b(bit_gd|gd)\b") || flat.Contains("do hoa") || flat.Contains("my thuat so")) return "BIT_GD";
        if (Regex.IsMatch(flat, @"\b(bba_ib|ib)\b") || flat.Contains("kinh doanh quoc te")) return "BBA_IB";
        if (Regex.IsMatch(flat, @"\b(bba_mc|mc)\b") || flat.Contains("truyen thong")) return "BBA_MC";
        if (Regex.IsMatch(flat, @"\b(bba_mkt|mkt)\b") || flat.Contains("marketing")) return "BBA_MKT";
        return null;
    }

    public async Task<AiConversationResponse> GetConversationHistoryAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var conv = await _ragRepository.GetConversationAsync(conversationId, userId, cancellationToken);
        if (conv == null)
        {
            throw new NotFoundException("Conversation", conversationId);
        }

        return new AiConversationResponse
        {
            Id = conv.Id,
            Title = conv.Title,
            CreatedAt = conv.CreatedAt,
            LastMessageAt = conv.LastMessageAt,
            Messages = conv.Messages.Select(m => new AiMessageResponse
            {
                Id = m.Id,
                Role = m.Role,
                Content = m.Content,
                Citations = DeserializeCitations(m.CitationsJson),
                CreatedAt = m.CreatedAt
            }).ToList()
        };
    }

    public async Task<IReadOnlyList<AiConversationResponse>> GetUserConversationsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var convs = await _ragRepository.GetConversationsByUserAsync(userId, cancellationToken);
        return convs.Select(c => new AiConversationResponse
        {
            Id = c.Id,
            Title = c.Title,
            CreatedAt = c.CreatedAt,
            LastMessageAt = c.LastMessageAt,
            Messages = new List<AiMessageResponse>()
        }).ToList();
    }

    private async Task<string> GenerateWithGeminiAsync(
        string question,
        IReadOnlyList<ScoredChunk> contextChunks,
        IEnumerable<AiMessage> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiModel}:generateContent?key={_geminiApiKey}";

            var sbContext = new StringBuilder();
            sbContext.AppendLine("NGỮ CẢNH TƯ LIỆU THỰC TẾ (TÀI LIỆU FPTU & BÀI ĐĂNG FORUM):");
            int i = 1;
            foreach (var sc in contextChunks)
            {
                sbContext.AppendLine($"[Tư liệu {i++} - Nguồn: {sc.Chunk.Document?.Title} | Mục: {sc.Chunk.Section}]");
                sbContext.AppendLine(sc.Chunk.Content);
                sbContext.AppendLine();
            }

            var systemInstruction = "Bạn là FPT Alumni Assistant chuyên nghiệp. Quy tắc bắt buộc: " +
                "1. Trả lời CHÍNH XÁC, ĐI THẲNG VÀO TRỌNG TÂM, KHÔNG LAN MAN, KHÔNG BỊA ĐẶT. " +
                "2. CHỈ sử dụng thông tin trong NGỮ CẢNH TƯ LIỆU được cung cấp bên dưới. " +
                "3. Nêu rõ các số liệu cụ thể (học phí, tín chỉ, mã môn, quy định) nếu có. " +
                "4. Cuối câu trả lời, hãy chỉ rõ nguồn tham khảo đã trích dẫn.";

            var currentPrompt = $"{systemInstruction}\n\n{sbContext}\n\nCÂU HỎI HIỆN TẠI CỦA NGƯỜI DÙNG: {question}";

            // Multi-turn conversation contents array (must begin with 'user' and strictly alternate 'user'/'model' for Gemini API)
            var contentsList = new List<object>();
            var recentHistory = history.OrderBy(m => m.CreatedAt).TakeLast(6).ToList();
            var alternatingHistory = new List<(string Role, string Content)>();
            foreach (var h in recentHistory)
            {
                var role = h.Role == "assistant" ? "model" : "user";
                if (alternatingHistory.Count == 0)
                {
                    if (role != "user") continue;
                    alternatingHistory.Add((role, h.Content));
                }
                else if (alternatingHistory[^1].Role == role)
                {
                    alternatingHistory[^1] = (role, alternatingHistory[^1].Content + "\n" + h.Content);
                }
                else
                {
                    alternatingHistory.Add((role, h.Content));
                }
            }
            while (alternatingHistory.Count > 0 && alternatingHistory[^1].Role != "model")
            {
                alternatingHistory.RemoveAt(alternatingHistory.Count - 1);
            }
            foreach (var h in alternatingHistory)
            {
                contentsList.Add(new
                {
                    role = h.Role,
                    parts = new[] { new { text = h.Content } }
                });
            }

            contentsList.Add(new
            {
                role = "user",
                parts = new[] { new { text = currentPrompt } }
            });

            var requestBody = new
            {
                contents = contentsList,
                generationConfig = new
                {
                    temperature = 0.2,
                    maxOutputTokens = 1200
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(responseJson);
                var candidates = doc.RootElement.GetProperty("candidates");
                if (candidates.GetArrayLength() > 0)
                {
                    var text = candidates[0]
                        .GetProperty("content")
                        .GetProperty("parts")[0]
                        .GetProperty("text")
                        .GetString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
            else
            {
                var err = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Gemini API returned error: {StatusCode} - {Body}. Falling back to local synthesizer.", response.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini call failed. Falling back to local synthesizer.");
        }

        return GenerateLocalGroundedAnswer(question, contextChunks);
    }

    private static string GenerateLocalGroundedAnswer(string question, IReadOnlyList<ScoredChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return "Chào bạn, hiện tại cơ sở dữ liệu FPT Alumni Connect chưa có tài liệu hoặc bài viết nào trực tiếp đề cập đến nội dung bạn hỏi. Bạn có thể đăng câu hỏi lên Diễn đàn (Forum) để các bạn sinh viên và cựu sinh viên khác giải đáp nhé!";
        }

        var sb = new StringBuilder();
        var primaryChunk = chunks[0].Chunk;

        sb.AppendLine($"Dựa trên tài liệu quy chuẩn của Đại học FPT (**{primaryChunk.Document?.Title ?? "FPTU Documents"}** - *{primaryChunk.Section}*), thông tin cụ thể như sau:\n");

        var cleanContent = primaryChunk.Content.Trim();
        if (cleanContent.Length > 2000)
        {
            cleanContent = cleanContent[..2000] + "...";
        }

        sb.AppendLine(cleanContent);
        sb.AppendLine();

        if (chunks.Count > 1)
        {
            sb.AppendLine("**Các thông tin bổ trợ liên quan:**");
            for (int i = 1; i < Math.Min(chunks.Count, 4); i++)
            {
                var extra = chunks[i].Chunk;
                var shortSnippet = GetCleanSnippet(extra.Content, 220);
                sb.AppendLine($"- **{extra.Document?.Title}** (*{extra.Section}*): {shortSnippet}");
            }
        }

        return sb.ToString().Trim();
    }

    private static string GetCleanSnippet(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = text.Replace("\n", " ").Replace("\r", " ").Trim();
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ");
        return clean.Length > maxLength ? clean[..maxLength] + "..." : clean;
    }

    private static List<CitationResponse> DeserializeCitations(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CitationResponse>>(json) ?? new List<CitationResponse>();
        }
        catch
        {
            return new List<CitationResponse>();
        }
    }
}
