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
                    u.AvailabilitySchedule,
                    HasAvatar = u.AvatarKey != null
                })
                .FirstOrDefaultAsync();

            if (user == null)
                return NotFound();

            return Ok(user);
        }

        // GET api/users — list users (optionally filtered by ?search= and/or ?group=), excluding current user
        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetUsers([FromQuery] string? search = null, [FromQuery] string? group = null)
        {
            var currentUserId = User.GetUserId();

            var query = _db.Users
                .Where(u => u.Id != currentUserId && u.Role != UserRole.Master);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(u =>
                    u.Username.ToLower().Contains(term) ||
                    (u.Email != null && u.Email.ToLower().Contains(term)));
            }

            if (!string.IsNullOrWhiteSpace(group))
            {
                var g = group.Trim().ToLower();
                query = query.Where(u => u.Group != null && u.Group.ToLower() == g);
            }

            var users = await query
                .OrderBy(u => u.Group)
                .ThenBy(u => u.Username)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Email,
                    Role = u.Role.ToString(),
                    u.Group,
                    u.AvailabilitySchedule
                })
                .ToListAsync();

            return Ok(users);
        }

        public record UpdateAvailabilityRequest(string? Schedule);

        [HttpPut("me/availability")]
        public async Task<IActionResult> UpdateAvailability([FromBody] UpdateAvailabilityRequest request)
        {
            var userId = User.GetUserId();
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.AvailabilitySchedule = string.IsNullOrWhiteSpace(request.Schedule) ? null : request.Schedule.Trim();

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

            const string defaultPassword = "Outsec@2026";

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
                    MustChangePassword = true,
                    Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim()
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();
            }
            else
            {
                // Re-invite: reset password so the email credentials work
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword);
                user.MustChangePassword = true;
                if (!string.IsNullOrWhiteSpace(request.Group))
                    user.Group = request.Group.Trim();
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

        // POST api/users/{id}/reset-password — Master only, resets to default temp password
        [HttpPost("{id:guid}/reset-password")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> ResetPassword(Guid id)
        {
            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            const string defaultPassword = "Outsec@2026";
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword);
            user.MustChangePassword = true;
            await _db.SaveChangesAsync();

            return Ok(new { message = $"Password reset to default for {user.Username}" });
        }

        public record SetPasswordRequest(string NewPassword);

        // POST api/users/{id}/set-password — Master only, set a specific password
        [HttpPost("{id:guid}/set-password")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> SetPassword(Guid id, [FromBody] SetPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.NewPassword) || request.NewPassword.Length < 8)
                return BadRequest("Password must be at least 8 characters.");

            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            user.MustChangePassword = false;
            await _db.SaveChangesAsync();

            return Ok(new { message = $"Password updated for {user.Username}" });
        }

        public record UpdateUserRequest(string Username, string Email, string Role, string? Group);

        // PUT api/users/{id} — Master only, edit user details
        [HttpPut("{id:guid}")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Username) || string.IsNullOrWhiteSpace(request.Email))
                return BadRequest("Username and email are required.");

            if (!Enum.TryParse<UserRole>(request.Role, out var role))
                return BadRequest("Invalid role.");

            // Prevent editing self (the Master account)
            var currentUserId = User.GetUserId();
            if (id == currentUserId)
                return BadRequest("You cannot edit your own account here.");

            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            // Check for username/email conflicts on other users
            var duplicate = await _db.Users.AnyAsync(u =>
                u.Id != id && (u.Username == request.Username.Trim() || u.Email == request.Email.Trim()));
            if (duplicate)
                return Conflict("Another user already has that username or email.");

            user.Username = request.Username.Trim();
            user.Email = request.Email.Trim();
            user.Role = role;
            user.Group = string.IsNullOrWhiteSpace(request.Group) ? null : request.Group.Trim();

            await _db.SaveChangesAsync();

            return Ok(new { message = "User updated." });
        }

        // DELETE api/users/{id} — Master only, delete a user
        [HttpDelete("{id:guid}")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> DeleteUser(Guid id)
        {
            var currentUserId = User.GetUserId();
            if (id == currentUserId)
                return BadRequest("You cannot delete your own account.");

            var user = await _db.Users.FindAsync(id);
            if (user == null) return NotFound();

            _db.Users.Remove(user);
            await _db.SaveChangesAsync();

            return Ok(new { message = $"User {user.Username} deleted." });
        }
    }
}
