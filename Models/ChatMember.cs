namespace Chat.Api.Models
{
    public class ChatMember
    {
        public Guid Id { get; set; }

        public Guid ChatId { get; set; }
        public Chat? Chat { get; set; }

        public Guid UserId { get; set; }
        public User? User { get; set; }

        public bool IsAdmin { get; set; } = false;

        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastReadAt { get; set; }
    }
}