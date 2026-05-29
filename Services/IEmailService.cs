namespace Chat.Api.Services
{
    public interface IEmailService
    {
        Task SendInviteAsync(string toEmail, string username, string appUrl);
        Task SendPasswordResetAsync(string toEmail, string username, string resetLink);
        Task SendMeetingInviteAsync(string toEmail, string username, string organiserName, string title, DateTime startsAt, DateTime endsAt, string appUrl);
    }
}
