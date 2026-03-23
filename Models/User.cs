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

        public DateTime? LastSeenAt { get; set; }

        [MaxLength(50)]
        public string? AvailabilityDays { get; set; }   // e.g. "Mon,Tue,Wed,Thu,Fri"

        [MaxLength(10)]
        public string? AvailabilityFrom { get; set; }   // e.g. "09:00"

        [MaxLength(10)]
        public string? AvailabilityTo { get; set; }     // e.g. "17:00"

        public bool MustChangePassword { get; set; } = false;

        [MaxLength(500)]
        public string? AvatarKey { get; set; }

        [MaxLength(100)]
        public string? Group { get; set; }

        public ICollection<ChatMember> ChatMembers { get; set; } = new List<ChatMember>();
        public ICollection<Message> Messages { get; set; } = new List<Message>();
    }
}