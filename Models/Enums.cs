namespace Chat.Api.Models
{
    public enum UserRole
    {
        Master,
        Admin,
        Staff
    }

    public enum MeetingInviteStatus
    {
        Pending  = 0,
        Accepted = 1,
        Declined = 2
    }
}
