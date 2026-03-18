namespace Chat.Api.Services
{
    public interface IEmailService
    {
        Task SendInviteAsync(string toEmail, string username, string appUrl);
    }
}
