using System.Net;
using System.Net.Mail;

namespace Chat.Api.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration config, ILogger<EmailService> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task SendInviteAsync(string toEmail, string username, string appUrl)
        {
            var smtp = _config.GetSection("Smtp");
            var host = smtp["Host"];
            var port = int.Parse(smtp["Port"] ?? "587");
            var smtpUser = smtp["Username"];
            var smtpPass = smtp["Password"];
            var fromName = smtp["FromName"] ?? "ImpTrack";
            var fromEmail = smtp["FromEmail"] ?? smtpUser ?? "noreply@imptrack.co.za";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                _logger.LogWarning("[EmailService] SMTP not configured — skipping invite email to {Email}", toEmail);
                return;
            }

            var subject = "You've been invited to ImpTrack Chat";
            var body = $@"Hi {username},

You have been invited to ImpTrack Chat.

To get started, visit: {appUrl}

Your login credentials:
  Username: {username}
  Temporary password: ImpTrack@2020

You will be prompted to change your password when you first log in.

Regards,
The ImpTrack Team";

            using var client = new SmtpClient(host, port)
            {
                Credentials = new NetworkCredential(smtpUser, smtpPass),
                EnableSsl = true
            };

            var message = new MailMessage(
                from: new MailAddress(fromEmail, fromName),
                to: new MailAddress(toEmail)
            )
            {
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };

            try
            {
                await client.SendMailAsync(message);
                _logger.LogInformation("[EmailService] Invite sent to {Email}", toEmail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EmailService] Failed to send invite to {Email}", toEmail);
                throw;
            }
        }
    }
}
