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

        public UsersController(ChatDbContext db, IEmailService email, IConfiguration config)
        {
            _db = db;
            _email = email;
            _config = config;
        }

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
                    u.AvailabilityTo
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

        // POST api/users/invite — Master only
        [HttpPost("invite")]
        [Authorize(Roles = "Master")]
        public async Task<IActionResult> InviteUser([FromBody] InviteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Email))
                return BadRequest("Username and email are required.");

            var username = request.Username.Trim();
            var email = request.Email.Trim();

            var exists = await _db.Users.AnyAsync(u => u.Username == username || u.Email == email);
            if (exists)
                return Conflict("Username or email already exists.");

            const string defaultPassword = "ImpTrack@2020";

            var user = new User
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

            var appUrl = _config["AppUrl"] ?? "https://main.d1imfsef8qotjc.amplifyapp.com";
            try
            {
                await _email.SendInviteAsync(email, username, appUrl);
            }
            catch (Exception ex)
            {
                // Don't fail the invite if email sending fails — user was created
                return Ok(new { message = $"User invited but email could not be sent: {ex.Message}" });
            }

            return Ok(new { message = $"Invitation sent to {email}" });
        }
    }
}
