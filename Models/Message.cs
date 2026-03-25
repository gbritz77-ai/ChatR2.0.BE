using System.Net.Mail;

namespace Chat.Api.Models
{
    public class Message
    {
        public Guid Id { get; set; }

        public Guid ChatId { get; set; }
        public Chat? Chat { get; set; }

        public Guid SenderId { get; set; }
        public User? Sender { get; set; }

        public string Text { get; set; } = string.Empty;
        public string? GifUrl { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsEdited { get; set; } = false;
        public DateTime? EditedAt { get; set; }

        public Guid? ReplyToMessageId { get; set; }
        public Message? ReplyToMessage { get; set; }

        // We can later add a MessageRead table per user; for now, keep it simple
        public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
        public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    }
}