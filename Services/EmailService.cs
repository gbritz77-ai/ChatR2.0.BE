using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

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
            var fromName = smtp["FromName"] ?? "ChatR";
            var fromEmail = smtp["FromEmail"] ?? smtpUser ?? "noreply@imptrack.co.za";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                _logger.LogWarning("[EmailService] SMTP not configured — skipping invite email to {Email}", toEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(username, toEmail));
            message.Subject = "You've been invited to ImpTrack Chat";
            message.Body = new TextPart("plain")
            {
                Text = $@"Hi {username},

You have been invited to ImpTrack Chat.

To get started, visit: {appUrl}

Your login credentials:
  Username: {username}
  Temporary password: Outsec@2026

You will be prompted to change your password when you first log in.

Regards,
Outsec Team"
            };

            // Port 465 = implicit SSL (SslOnConnect), port 587 = STARTTLS (StartTls)
            var socketOptions = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

            using var client = new SmtpClient();
            // Accept self-signed or untrusted certs on private mail servers
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await client.ConnectAsync(host, port, socketOptions);
            await client.AuthenticateAsync(smtpUser, smtpPass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("[EmailService] Invite sent to {Email}", toEmail);
        }
    }
}
