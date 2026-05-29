namespace Chat.Api.Models
{
    public class MeetingInvite
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid MeetingId { get; set; }
        public Meeting? Meeting { get; set; }

        public Guid UserId { get; set; }
        public User? User { get; set; }

        public MeetingInviteStatus Status { get; set; } = MeetingInviteStatus.Pending;
    }
}
