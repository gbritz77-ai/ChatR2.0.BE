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

        // We can later add a MessageRead table per user; for now, keep it simple
        public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
    }
}