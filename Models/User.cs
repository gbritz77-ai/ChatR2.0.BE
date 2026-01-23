using System.ComponentModel.DataAnnotations;

namespace Chat.Api.Models
{
    public class User
    {
        public Guid Id { get; set; }

        [MaxLength(100)]
        public string Username { get; set; } = string.Empty;

        [MaxLength(200)]
        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        public UserRole Role { get; set; } = UserRole.Staff;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<ChatMember> ChatMembers { get; set; } = new List<ChatMember>();
        public ICollection<Message> Messages { get; set; } = new List<Message>();
    }
}