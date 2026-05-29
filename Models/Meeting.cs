using System.ComponentModel.DataAnnotations;

namespace Chat.Api.Models
{
    public class Meeting
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [MaxLength(200)]
        public string Title { get; set; } = string.Empty;

        public DateTime StartsAt { get; set; }
        public DateTime EndsAt { get; set; }

        public Guid CreatedByUserId { get; set; }
        public User? CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ICollection<MeetingInvite> Invites { get; set; } = new List<MeetingInvite>();
    }
}
