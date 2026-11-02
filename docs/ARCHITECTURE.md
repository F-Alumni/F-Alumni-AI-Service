# 🏛️ Sơ Đồ Kiến Trúc Hệ Thống Chi Tiết — AI Service (`f-alumni-ai-service`)

> **Người thực hiện**: Vinh (`vinhmt` — Backend 2)  
> **Cổng dịch vụ**: `http://localhost:5004`  
> **Kiến trúc**: 3 tầng `.NET 10` (`Api -> Services -> Repositories`), Hybrid RAG Retrieval, Gemini LLM API, Local Grounded Fallback Synthesizer, Mentor Recommendation.

---

## 1. Sơ Đồ Kiến Trúc RAG & Tổng Thể Hệ Thống

![AI Service Architecture](architecture/f-alumni-be2-architecture.svg)

---

## 2. Đường Ống Nạp Dữ Liệu RAG (RAG Ingestion Pipeline)

```mermaid
flowchart TD
    DocxFiles["12 File Word (.docx)\n(Curriculums SE, AI, IA, GD, IB, MC, MKT;\nFacilities; Tuition; Slang)"]
    StartupWorker["RagStartupWorker\n(Chạy tự động khi khởi động)"]
    DocxParser["Docx Text & Embedded JSON Parser\n(Tự động bóc tách cấu trúc đoạn)"]
    ChunkEngine["RagIngestionService\n(Sinh đoạn tri thức, gắn nhãn Major, Campus, Keywords)"]
    AIDb[("FAlumni_AiService_Db\n(RagDocuments & RagChunks)")]

    DocxFiles --> StartupWorker
    StartupWorker --> DocxParser
    DocxParser --> ChunkEngine
    ChunkEngine --> AIDb
```

---

## 3. Luồng Hỏi Đáp AI Đa Lượt & Trích Dẫn Nguồn (Sequence Diagram)

```mermaid
sequenceDiagram
    autonumber
    actor Student as Sinh viên / Người dùng
    participant Gateway as YARP Gateway (:5000)
    participant AiApi as AiChatController (:5004)
    participant ChatSvc as AiChatService
    participant SearchSvc as RagSearchService
    participant DB as FAlumni_AiService_Db
    participant Gemini as Google Gemini API (v1beta)

    Student->>Gateway: POST /api/v1/ai/chat (Bearer JWT, Question, ConversationId)
    Gateway->>AiApi: Chuyển tiếp request
    AiApi->>ChatSvc: AskAsync(request, userId)
    ChatSvc->>DB: Lấy/Tạo AiConversation và lưu tin nhắn User
    ChatSvc->>SearchSvc: SearchRelevantChunksAsync(question, major, campus)
    SearchSvc->>DB: Truy vấn RagChunks kết hợp lọc Major/Campus
    SearchSvc-->>ChatSvc: Top K ScoredChunks (Kèm điểm số tương đồng)
    
    alt Có cấu hình Gemini API Key
        ChatSvc->>Gemini: POST generateContent (Prompt ngữ cảnh + Lịch sử hội thoại)
        Gemini-->>ChatSvc: 200 OK (Câu trả lời tự nhiên, chính xác)
    else Không có API Key hoặc Gemini lỗi
        ChatSvc->>ChatSvc: Tự động chạy Local Grounded Synthesizer
        Note over ChatSvc: Tổng hợp câu trả lời dựa trên trích đoạn thực tế FPTU
    end

    ChatSvc->>DB: Lưu AiMessage (Role: Assistant, CitationsJson)
    ChatSvc-->>AiApi: AiChatResponseDto (Answer, Citations, ConversationId)
    AiApi-->>Gateway: 200 OK
    Gateway-->>Student: 200 OK (Hiển thị câu trả lời + Link trích dẫn nguồn)
```
