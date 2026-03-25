namespace Chat.Api.Models
{
    public class MessageReaction
    {
        public Guid Id { get; set; }
        public Guid MessageId { get; set; }
        public Message? Message { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }
        public string Emoji { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
