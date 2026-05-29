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
            var fromName = smtp["FromName"] ?? "Chat Hub";
            var fromEmail = smtp["FromEmail"] ?? smtpUser ?? "noreply@imptrack.co.za";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                _logger.LogWarning("[EmailService] SMTP not configured — skipping invite email to {Email}", toEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(username, toEmail));
            message.Subject = "You've been invited to Outsec Chat Hub";
            message.Body = new TextPart("plain")
            {
                Text = $@"Hi {username},

You have been invited to Outsec Chat Hub.

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

        public async Task SendPasswordResetAsync(string toEmail, string username, string resetLink)
        {
            var smtp = _config.GetSection("Smtp");
            var host = smtp["Host"];
            var port = int.Parse(smtp["Port"] ?? "587");
            var smtpUser = smtp["Username"];
            var smtpPass = smtp["Password"];
            var fromName = smtp["FromName"] ?? "Chat Hub";
            var fromEmail = smtp["FromEmail"] ?? smtpUser ?? "noreply@imptrack.co.za";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                _logger.LogWarning("[EmailService] SMTP not configured — skipping password reset email to {Email}", toEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(username, toEmail));
            message.Subject = "ChatHub — Password Reset";
            message.Body = new TextPart("plain")
            {
                Text = $@"Hi {username},

We received a request to reset your ChatHub password.

Click the link below to set a new password (valid for 1 hour):

{resetLink}

If you did not request a password reset, please ignore this email — your password will remain unchanged.

Regards,
Outsec Team"
            };

            var socketOptions = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

            using var client = new SmtpClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await client.ConnectAsync(host, port, socketOptions);
            await client.AuthenticateAsync(smtpUser, smtpPass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("[EmailService] Password reset email sent to {Email}", toEmail);
        }

        public async Task SendMeetingInviteAsync(string toEmail, string username, string organiserName, string title, DateTime startsAt, DateTime endsAt, string appUrl)
        {
            var smtp = _config.GetSection("Smtp");
            var host = smtp["Host"];
            var port = int.Parse(smtp["Port"] ?? "587");
            var smtpUser = smtp["Username"];
            var smtpPass = smtp["Password"];
            var fromName = smtp["FromName"] ?? "Chat Hub";
            var fromEmail = smtp["FromEmail"] ?? smtpUser ?? "noreply@imptrack.co.za";

            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
            {
                _logger.LogWarning("[EmailService] SMTP not configured — skipping meeting invite email to {Email}", toEmail);
                return;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(username, toEmail));
            message.Subject = $"Meeting Invite: {title}";
            message.Body = new TextPart("plain")
            {
                Text = $@"Hi {username},

{organiserName} has invited you to a meeting.

Meeting: {title}
Date:    {startsAt.ToLocalTime():dddd, dd MMMM yyyy}
Time:    {startsAt.ToLocalTime():HH:mm} – {endsAt.ToLocalTime():HH:mm}

To view and respond to the invite, visit:
{appUrl}

Regards,
Outsec Team"
            };

            var socketOptions = port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

            using var client = new SmtpClient();
            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            await client.ConnectAsync(host, port, socketOptions);
            await client.AuthenticateAsync(smtpUser, smtpPass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("[EmailService] Meeting invite sent to {Email}", toEmail);
        }
    }
}
