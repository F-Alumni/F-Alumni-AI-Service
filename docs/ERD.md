# 🗄️ Sơ Đồ ERD Cơ Sở Dữ Liệu — AI Service (`FAlumni_AiService_Db`)

> **Service**: `f-alumni-ai-service`  
> **Người phụ trách**: Vinh (`vinhmt` — Backend 2)  
> **Cơ sở dữ liệu**: `FAlumni_AiService_Db` (SQL Server + EF Core 10)  
> **Nguyên tắc kiến trúc**: Database-per-Service — Lưu trữ kho tri thức RAG (chunks, embeddings/tags, documents), phiên hội thoại AI đa lượt (`AiConversations`), lịch sử tin nhắn (`AiMessages`), và danh bạ hồ sơ cố vấn (`MentorProfiles`).

---

## 1. Sơ Đồ ERD Chuẩn Mermaid (`FAlumni_AiService_Db`)

```mermaid
erDiagram
    RagDocuments ||--o{ RagChunks : "1:N (DocumentId, Cascade)"
    AiConversations ||--o{ AiMessages : "1:N (ConversationId, Cascade)"

    RagDocuments {
        uniqueidentifier Id PK "Guid.NewGuid()"
        nvarchar_200 Title "NOT NULL"
        nvarchar_100 DocumentType "NOT NULL (Curriculum | Facilities | Tuition | Slang | ForumPost)"
        nvarchar_500 SourceFilePath "NULL"
        nvarchar_50 Major "NOT NULL, Default 'ALL' (SE | AI | IA | GD | IB | MC | MKT | ALL)"
        nvarchar_50 Campus "NOT NULL, Default 'ALL'"
        int ChunkCount "NOT NULL, Default 0"
        datetime2 IngestedAtUtc "NOT NULL"
    }

    RagChunks {
        uniqueidentifier Id PK "Guid.NewGuid()"
        uniqueidentifier DocumentId FK "NOT NULL -> RagDocuments.Id"
        int ChunkIndex "NOT NULL"
        nvarchar_200 Section "NOT NULL (Tên môn, tiêu đề mục)"
        nvarchar_max Content "NOT NULL (Nội dung chi tiết)"
        nvarchar_1000 Keywords "NOT NULL (Từ khóa tìm kiếm)"
        nvarchar_50 Major "NOT NULL, Default 'ALL'"
        nvarchar_50 Campus "NOT NULL, Default 'ALL'"
        datetime2 CreatedAtUtc "NOT NULL"
    }

    AiConversations {
        uniqueidentifier Id PK "Guid.NewGuid()"
        uniqueidentifier UserId "NOT NULL (Pure Guid)"
        nvarchar_200 Title "NOT NULL, Default 'Cuoc tro chuyen moi'"
        datetime2 CreatedAtUtc "NOT NULL"
        datetime2 LastActiveAtUtc "NOT NULL"
    }

    AiMessages {
        uniqueidentifier Id PK "Guid.NewGuid()"
        uniqueidentifier ConversationId FK "NOT NULL -> AiConversations.Id"
        nvarchar_30 Role "NOT NULL (user | assistant | system)"
        nvarchar_max Content "NOT NULL"
        nvarchar_max CitationsJson "NULL (Danh sách nguồn trích dẫn JSON)"
        datetime2 CreatedAtUtc "NOT NULL"
    }

    MentorProfiles {
        uniqueidentifier Id PK "Guid.NewGuid()"
        uniqueidentifier UserId "NOT NULL, Unique"
        nvarchar_150 FullName "NOT NULL"
        nvarchar_150 CurrentCompany "NOT NULL"
        nvarchar_150 JobTitle "NOT NULL"
        nvarchar_50 Major "NOT NULL"
        nvarchar_50 Campus "NOT NULL"
        nvarchar_1000 ExpertiseKeywords "NOT NULL"
        nvarchar_1000 Bio "NOT NULL"
        nvarchar_500 GoogleCalendarUrl "NOT NULL"
        bit IsAvailableForMentoring "NOT NULL, Default 1"
        datetime2 CreatedAtUtc "NOT NULL"
    }
```

---

## 2. Các Chỉ Mục & Khóa Ràng Buộc Tối Ưu Hóa (Indexes)

1. **RagChunks(Major, Campus)**: Giúp lọc tức thì kho tri thức theo ngành học và campus trước khi tính điểm xếp hạng độ tương đồng RAG.
2. **RagChunks(DocumentId, ChunkIndex)**: Đảm bảo thứ tự xuất hiện của các đoạn nội dung trong tài liệu gốc.
3. **AiMessages(ConversationId, CreatedAtUtc ASC)**: Truy xuất toàn bộ lịch sử ngữ cảnh đa lượt (Multi-turn Context) với độ trễ thấp nhất.
4. **MentorProfiles(Major, IsAvailableForMentoring)**: Phục vụ truy vấn gợi ý cố vấn theo chuyên ngành sinh viên.

