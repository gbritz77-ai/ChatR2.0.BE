namespace Chat.Api.Models
{
    public class Chat
    {
        public Guid Id { get; set; }

        // 1-1 chats: IsGroup = false, Name can be null
        // Group chats: IsGroup = true, Name required
        // Group chats: IsGroup = true, Name required
        public bool IsGroup { get; set; }

        public string? Name { get; set; }

        public Guid CreatedByUserId { get; set; }
        public User? CreatedByUser { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ChatMember> Members { get; set; } = new List<ChatMember>();
        public ICollection<Message> Messages { get; set; } = new List<Message>();
        public string? PrivateKey { get; set; } // null for groups

        [System.ComponentModel.DataAnnotations.MaxLength(500)]
        public string? AvatarKey { get; set; }

    }
}