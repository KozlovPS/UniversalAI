namespace UniversalAI.Data.Entities;

public class ChatMessageEntity
{
    public int Id { get; set; }

    public int ConversationId { get; set; }

    public string Role { get; set; } = "";

    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ConversationEntity? Conversation { get; set; }
}