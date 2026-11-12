using System.Net;
using System.Net.Http.Json;
using AiService.Services.DTOs.Requests;
using AiService.Services.DTOs.Responses;
using Xunit;

namespace AiService.Tests.Endpoints;

public class BoostEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public BoostEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task BoostGetEndpoint_DefaultQuery_ReturnsDiagnosticsWith200()
    {
        // Act
        var response = await _client.GetAsync("/boost");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(result);
        Assert.NotEmpty(result.Query);
        Assert.Contains(result.DetectedIntents, i => i.StartsWith("Tuition"));
    }

    [Fact]
    public async Task BoostGetEndpoint_CustomParam_ReturnsMatchedIntents()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/ai/boost?q=vovinam%20nam%20nhat&campus=FU-HL");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(result);
        Assert.Contains(result.DetectedIntents, i => i.StartsWith("FirstYearSubjects"));
    }

    [Fact]
    public async Task BoostPostEndpoint_JsonPayload_ReturnsMatchedIntents()
    {
        // Arrange
        var request = new BoostTestRequest
        {
            Query = "Kỹ thuật phần mềm BIT_SE cần bao nhiêu tín chỉ?",
            Major = "SE",
            Campus = "FU-HL",
            TopK = 3
        };

        // Act
        var response = await _client.PostAsJsonAsync("/boost", request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(result);
        Assert.Contains(result.DetectedIntents, i => i.StartsWith("Curriculum"));
    }

    [Fact]
    public async Task EndToEnd_Ingest_Then_Boost_And_Chat_ReturnsGroundedResults()
    {
        // 1. Trigger ingestion into InMemory DB
        var ingestResp = await _client.PostAsJsonAsync("/api/v1/ai/rag/ingest", new IngestRagRequest { ForceReindex = true });
        Assert.Equal(HttpStatusCode.OK, ingestResp.StatusCode);
        var ingestResult = await ingestResp.Content.ReadFromJsonAsync<RagIngestionResult>();
        Assert.NotNull(ingestResult);
        Assert.Equal(12, ingestResult.TotalFilesProcessed);

        // 2. Test /boost with short Major "SE" and campus "FU-HL" (previously broken!)
        var boostResp = await _client.PostAsJsonAsync("/boost", new BoostTestRequest
        {
            Query = "Kỹ thuật phần mềm học bao nhiêu tín chỉ?",
            Major = "SE",
            Campus = "FU-HL",
            TopK = 3
        });
        Assert.Equal(HttpStatusCode.OK, boostResp.StatusCode);
        var boostData = await boostResp.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(boostData);
        Assert.NotEmpty(boostData.TopResults);
        Assert.Equal("BIT_SE", boostData.TopResults[0].ProgramCode);

        // 3. Test /api/v1/ai/chat unaccented query
        var chatResp = await _client.PostAsJsonAsync("/api/v1/ai/chat", new AiChatRequest
        {
            Question = "hoc phi tai hola bao nhieu",
            Campus = "FU-HL",
            Major = "SE"
        });
        Assert.Equal(HttpStatusCode.OK, chatResp.StatusCode);
        var chatData = await chatResp.Content.ReadFromJsonAsync<AiChatResponse>();
        Assert.NotNull(chatData);
        Assert.NotEmpty(chatData.Citations);
        Assert.Contains("Học phí", chatData.Citations[0].SourceTitle);

        // 4. Test /api/v1/ai/boost/test with FirstYearSubjects + unaccented query & campus alias normalization
        var firstYearResp = await _client.GetAsync("/api/v1/ai/boost/test?q=vovinam%20va%20tieng%20anh%20nam%20nhat&campus=Hola&topK=0");
        Assert.Equal(HttpStatusCode.OK, firstYearResp.StatusCode);
        var firstYearBoost = await firstYearResp.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(firstYearBoost);
        Assert.Contains(firstYearBoost.DetectedIntents, i => i.StartsWith("FirstYearSubjects"));
        Assert.NotEmpty(firstYearBoost.TopResults);
        Assert.True(firstYearBoost.TopResults[0].Score >= 45.0);

        // 5. Test unmatched gibberish query returns 200 OK cleanly with empty TopResults
        var emptyBoostResp = await _client.GetAsync("/boost?q=xyzqwerty987654&campus=FU-HL");
        Assert.Equal(HttpStatusCode.OK, emptyBoostResp.StatusCode);
        var emptyBoost = await emptyBoostResp.Content.ReadFromJsonAsync<RagBoostDiagnosticsResponse>();
        Assert.NotNull(emptyBoost);
        Assert.Empty(emptyBoost.DetectedIntents);
        Assert.Empty(emptyBoost.TopResults);
    }
}
