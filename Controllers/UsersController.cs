using Amazon.S3;
using Amazon.S3.Model;
using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.DTOs.Auth;
using Chat.Api.Models;
using Chat.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IEmailService _email;
        private readonly IConfiguration _config;
        private readonly IAmazonS3 _s3;
        private readonly ILogger<UsersController> _logger;

        public UsersController(ChatDbContext db, IEmailService email, IConfiguration config, IAmazonS3 s3, ILogger<UsersController> logger)
        {
            _db = db;
            _email = email;
            _config = config;
            _s3 = s3;
            _logger = logger;
        }

        private string? GetBucketName() =>
            _config["AWS:S3Bucket"] ?? _config["AWS__S3Bucket"];

        // GET api/users/me
        [HttpGet("me")]
        public async Task<ActionResult<object>> GetMe()
        {
            var userId = User.GetUserId();

            var user = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Email,
                    Role = u.Role.ToString(),
                    u.CreatedAt,
                    u.AvailabilityDays,
                    u.AvailabilityFrom,
                    u.AvailabilityTo,
                    HasAvatar = u.AvatarKey != null
                })
                .FirstOrDefaultAsync();

            if (user == null)
                return NotFound();

            return Ok(user);
        }

        // GET api/users — list users (optionally filtered by ?search=), excluding current user
        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetUsers([FromQuery] string? search = null)
        {
            var currentUserId = User.GetUserId();

            var query = _db.Users
                .Where(u => u.Id != currentUserId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(u =>
                    u.Username.ToLower().Contains(term) ||
                    (u.Email != null && u.Email.ToLower().Contains(term)));
            }

            var users = await query
                .OrderBy(u => u.Username)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Email,
                    Role = u.Role.ToString()
                })
                .ToListAsync();

            return Ok(users);
        }

        public record UpdateAvailabilityRequest(string? Days, string? From, string? To);

        [HttpPut("me/availability")]
        public async Task<IActionResult> UpdateAvailability([FromBody] UpdateAvailabilityRequest request)
        {
            var userId = User.GetUserId();
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.AvailabilityDays = string.IsNullOrWhiteSpace(request.Days) ? null : request.Days.Trim();
            user.AvailabilityFrom = string.IsNullOrWhiteSpace(request.From) ? null : request.From.Trim();
            user.AvailabilityTo   = string.IsNullOrWhiteSpace(request.To)   ? null : request.To.Trim();

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // ── Avatar endpoints ─────────────────────────────────────────────────

        // GET api/users/me/avatar-upload-url?contentType=image/jpeg
        [HttpGet("me/avatar-upload-url")]
        public ActionResult<object> GetAvatarUploadUrl([FromQuery] string contentType = "image/jpeg")
        {
            var userId = User.GetUserId();

            var bucket = GetBucketName();
            if (string.IsNullOrWhiteSpace(bucket))
                return StatusCode(500, "S3 bucket not configured (AWS:S3Bucket)");

            var key = $"avatars/{userId}";
            var expiresAt = DateTime.UtcNow.AddMinutes(10);

            var presignReq = new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = expiresAt,
                ContentType = contentType
            };

            var uploadUrl = _s3.GetPreSignedURL(presignReq);

            return Ok(new { uploadUrl, key, expiresAt });
        }

        public record ConfirmAvatarRequest(string Key);

        // POST api/users/me/avatar — saves the S3 key after a successful upload
        [HttpPost("me/avatar")]
        public async Task<IActionResult> ConfirmAvatar([FromBody] ConfirmAvatarRequest request)
        {
            var userId = User.GetUserId();
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.AvatarKey = request.Key;
            await _db.SaveChangesAsync();

            return Ok(new { avatarKey = user.AvatarKey });
        }

        // GET api/users/{userId}/avatar-url — returns a presigned GET URL (1 hr TTL)
        [HttpGet("{userId:guid}/avatar-url")]
        public async Task<ActionResult<object>> GetAvatarUrl(Guid userId)
        {
            var user = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.AvatarKey })
                .FirstOrDefaultAsync();

            if (user == null || string.IsNullOrWhiteSpace(user.AvatarKey))
                return NotFound();

            var bucket = GetBucketName();
            if (string.IsNullOrWhiteSpace(bucket))
                return StatusCode(500, "S3 bucket not configured (AWS:S3Bucket)");

            var expiresAt = DateTime.UtcNow.AddHours(1);

            var presignReq = new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = user.AvatarKey,
                Verb = HttpVerb.GET,
                Expires = expiresAt
            };

            var url = _s3.GetPreSignedURL(presignReq);

            return Ok(new { url, expiresAt });
        }

        // ── Invite endpoint ──────────────────────────────────────────────────

        // POST api/users/invite — Master only
        [HttpPost("invite")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> InviteUser([FromBody] InviteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Email))
                return BadRequest("Username and email are required.");

            var username = request.Username.Trim();
            var email = request.Email.Trim();

            const string defaultPassword = "ImpTrack@2020";

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username || u.Email == email);
            if (user == null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword),
                    Role = UserRole.Staff,
                    MustChangePassword = true
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }

            var appUrl = _config["AppUrl"] ?? "https://main.d1imfsef8qotjc.amplifyapp.com";
            try
            {
                await _email.SendInviteAsync(email, username, appUrl);
            }
            catch (Exception ex)
            {
                // Don't fail the invite if email sending fails — user was created
                _logger.LogError(ex, "[Invite] SMTP failed for {Email}: {Message}", email, ex.Message);
                return Ok(new { message = $"User invited but email could not be sent: {ex.Message}" });
            }

            return Ok(new { message = $"Invitation sent to {email}" });
        }
    }
}
