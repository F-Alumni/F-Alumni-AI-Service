using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AiService.Repositories.Entities;
using AiService.Repositories.Interfaces;
using AiService.Services.DTOs.Responses;
using AiService.Services.Interfaces;

namespace AiService.Services.Implementations;

public class RagSearchService : IRagSearchService
{
    private readonly IRagRepository _ragRepository;
    private readonly ILogger<RagSearchService> _logger;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "la", "là", "cua", "của", "va", "và", "cac", "các", "nhung", "những", "thi", "thì",
        "ma", "mà", "o", "ở", "tai", "tại", "cho", "voi", "với", "duoc", "được", "co", "có",
        "gi", "gì", "nao", "nào", "sao", "the", "thế", "nhu", "như", "hoi", "hỏi", "ve", "về",
        "trong", "minh", "mình", "xin", "bao", "nhieu", "nhiêu", "may", "mấy", "lam", "làm",
        "nay", "này", "do", "đó", "a", "an", "in", "on", "at", "is", "are", "what", "how",
        "nha", "nhe", "nhé", "vậy", "vay", "a", "ạ", "ơi", "oi", "ban", "bạn"
    };

    public RagSearchService(IRagRepository ragRepository, ILogger<RagSearchService> logger)
    {
        _ragRepository = ragRepository;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchRelevantChunksAsync(
        string query,
        string? programCode = null,
        string? campus = null,
        int topK = 4,
        CancellationToken cancellationToken = default)
    {
        var (_, results) = await EvaluateAsync(query, programCode, campus, topK, cancellationToken);
        return results;
    }

    public async Task<RagBoostDiagnosticsResponse> TestBoostAsync(
        string query,
        string? programCode = null,
        string? campus = null,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var (diagnostics, _) = await EvaluateAsync(query, programCode, campus, topK, cancellationToken);
        return diagnostics;
    }

    private async Task<(RagBoostDiagnosticsResponse Diagnostics, IReadOnlyList<ScoredChunk> Results)> EvaluateAsync(
        string query,
        string? programCode,
        string? campus,
        int topK,
        CancellationToken cancellationToken)
    {
        var diagnostics = new RagBoostDiagnosticsResponse
        {
            Query = query ?? string.Empty
        };

        if (string.IsNullOrWhiteSpace(query))
        {
            return (diagnostics, Array.Empty<ScoredChunk>());
        }

        var normalizedQuery = query.Trim().ToLowerInvariant();
        var unaccentedQuery = RemoveDiacritics(normalizedQuery);
        diagnostics.NormalizedQuery = unaccentedQuery;

        var queryTerms = Regex.Split(unaccentedQuery, @"[^\p{L}\p{N}_]+")
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        diagnostics.ExtractedTerms = queryTerms;

        // Detect query intents
        bool isTuitionIntent = ContainsAny(unaccentedQuery, "hoc phi", "tuition", "tien hoc", "dong tien", "nop tien", "hoc bong", "mien giam");
        bool isFirstYearIntent = ContainsAny(unaccentedQuery, "vovinam", "nam nhat", "tieng anh", "quan su", "ren luyen", "the duc", "coursera", "dinh huong", "luk", "transition");
        bool isFacilitiesOrSlangIntent = ContainsAny(unaccentedQuery, "hola", "hoa lac", "xavalo", "hovilo", "fuda", "xe buyt", "xe bus", "ky tuc", "ktx", "dom", "co so vat chat", "thu vien", "can tin", "san bong");
        bool isForumIntent = ContainsAny(unaccentedQuery, "kinh nghiem", "chia se", "review", "forum", "bai viet", "cuu sinh vien", "ojt", "thuc tap");
        bool isCurriculumIntent = ContainsAny(unaccentedQuery, "tin chi", "khung chuong trinh", "chuong trinh dao tao", "muc tieu", "chuan dau ra", "mon hoc", "chuyen nganh", "plo", "ky hoc", "hoc gi", "phan mem", "tri tue nhan tao", "an toan thong tin", "do hoa", "kinh doanh quoc te", "truyen thong", "marketing");

        if (isTuitionIntent) diagnostics.DetectedIntents.Add("Tuition (+45)");
        if (isFirstYearIntent) diagnostics.DetectedIntents.Add("FirstYearSubjects (+45)");
        if (isFacilitiesOrSlangIntent) diagnostics.DetectedIntents.Add("Facilities/Slang (+40)");
        if (isForumIntent) diagnostics.DetectedIntents.Add("ForumPost (+35)");
        if (isCurriculumIntent) diagnostics.DetectedIntents.Add("Curriculum (+40)");

        var allChunks = await _ragRepository.GetAllChunksAsync(programCode, campus, cancellationToken);
        diagnostics.TotalCandidateChunks = allChunks.Count;

        if (allChunks.Count == 0)
        {
            return (diagnostics, Array.Empty<ScoredChunk>());
        }

        var scoredList = new List<ScoredChunk>();

        foreach (var chunk in allChunks)
        {
            double score = 0;
            var matchedIntents = new List<string>();

            var docTitle = (chunk.Document?.Title ?? string.Empty).ToLowerInvariant();
            var docType = (chunk.Document?.DocumentType ?? string.Empty).ToLowerInvariant();
            var docProgram = (chunk.Document?.ProgramCode ?? string.Empty).ToLowerInvariant();
            var section = chunk.Section.ToLowerInvariant();
            var content = chunk.Content.ToLowerInvariant();
            var keywords = chunk.Keywords.ToLowerInvariant();

            var docTitleFlat = RemoveDiacritics(docTitle);
            var sectionFlat = RemoveDiacritics(section);
            var contentFlat = RemoveDiacritics(content);
            var keywordsFlat = RemoveDiacritics(keywords);

            // 1. Exact phrase matching
            if (content.Contains(normalizedQuery) || contentFlat.Contains(unaccentedQuery))
            {
                score += 50.0;
                matchedIntents.Add("ExactPhrase (+50)");
            }

            // 2. Intent boosting
            if (isTuitionIntent && docType == "tuition")
            {
                score += 45.0;
                matchedIntents.Add("TuitionBoost (+45)");
            }

            if (isFirstYearIntent && (docType == "firstyearsubjects" || contentFlat.Contains("vovinam") || contentFlat.Contains("coursera")))
            {
                score += 45.0;
                matchedIntents.Add("FirstYearBoost (+45)");
            }

            if (isFacilitiesOrSlangIntent && (docType == "slang" || docType == "facilities" || contentFlat.Contains("hola") || contentFlat.Contains("xavalo")))
            {
                score += 40.0;
                matchedIntents.Add("FacilitiesSlangBoost (+40)");
            }

            if (isForumIntent && docType == "forumpost")
            {
                score += 35.0;
                matchedIntents.Add("ForumBoost (+35)");
            }

            if (isCurriculumIntent && docType == "curriculum")
            {
                score += 40.0;
                matchedIntents.Add("CurriculumBoost (+40)");
            }

            // Program code boosting (using safe word-boundary checks)
            if (!string.IsNullOrEmpty(docProgram))
            {
                bool programMatch = false;
                if (Regex.IsMatch(unaccentedQuery, $@"\b{Regex.Escape(docProgram)}\b"))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("se") && (Regex.IsMatch(unaccentedQuery, @"\b(se|ktpm)\b") || ContainsAny(unaccentedQuery, "phan mem", "software")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("ai") && (Regex.IsMatch(unaccentedQuery, @"\b(bit_ai)\b") || ContainsAny(unaccentedQuery, "tri tue nhan tao", "artificial intelligence", "nganh ai")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("ia") && (Regex.IsMatch(unaccentedQuery, @"\b(ia|attt)\b") || ContainsAny(unaccentedQuery, "an toan thong tin", "bao mat", "information assurance")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("gd") && (Regex.IsMatch(unaccentedQuery, @"\bgd\b") || ContainsAny(unaccentedQuery, "do hoa", "my thuat so", "graphic design")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("ib") && (Regex.IsMatch(unaccentedQuery, @"\bib\b") || ContainsAny(unaccentedQuery, "kinh doanh quoc te", "international business")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("mc") && (Regex.IsMatch(unaccentedQuery, @"\bmc\b") || ContainsAny(unaccentedQuery, "truyen thong", "multimedia")))
                {
                    programMatch = true;
                }
                else if (docProgram.Contains("mkt") && (Regex.IsMatch(unaccentedQuery, @"\bmkt\b") || ContainsAny(unaccentedQuery, "marketing")))
                {
                    programMatch = true;
                }

                if (programMatch)
                {
                    score += 35.0;
                    matchedIntents.Add($"ProgramMatch:{docProgram.ToUpperInvariant()} (+35)");
                }
            }

            // 3. Keyword / Term matching (diacritics-folded for high recall)
            foreach (var term in queryTerms)
            {
                if (docTitleFlat.Contains(term)) score += 12.0;
                if (sectionFlat.Contains(term)) score += 8.0;
                if (keywordsFlat.Contains(term)) score += 6.0;

                int matches = Regex.Matches(contentFlat, Regex.Escape(term)).Count;
                if (matches > 0)
                {
                    score += Math.Min(matches, 5) * 3.0;
                }
            }

            if (score > 2.0)
            {
                scoredList.Add(new ScoredChunk
                {
                    Chunk = chunk,
                    Score = score,
                    MatchedIntent = matchedIntents.Count > 0 ? string.Join(", ", matchedIntents) : "LexicalMatch"
                });
            }
        }

        var results = scoredList
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .ToList();

        diagnostics.TopResults = results.Select(r => new ScoredChunkDetail
        {
            Score = r.Score,
            DocumentTitle = r.Chunk.Document?.Title ?? string.Empty,
            DocumentType = r.Chunk.Document?.DocumentType ?? string.Empty,
            ProgramCode = r.Chunk.Document?.ProgramCode,
            Campus = r.Chunk.Document?.Campus ?? "All",
            Section = r.Chunk.Section,
            MatchedIntent = r.MatchedIntent,
            Snippet = r.Chunk.Content.Length > 260 ? r.Chunk.Content[..260] + "..." : r.Chunk.Content
        }).ToList();

        _logger.LogInformation("Tìm kiếm RAG '{Query}': trả về {Count} đoạn tri thức liên quan (Top score: {TopScore})",
            query, results.Count, results.FirstOrDefault()?.Score ?? 0);

        return (diagnostics, results);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch switch
                {
                    'đ' => 'd',
                    'Đ' => 'D',
                    _ => ch
                });
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
