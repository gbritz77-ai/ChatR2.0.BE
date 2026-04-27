using BCrypt.Net;
using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.DTOs.Auth;
using Chat.Api.Models;
using Chat.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Chat.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IConfiguration _config;
        private readonly IEmailService _email;

        public AuthController(ChatDbContext db, IConfiguration config, IEmailService email)
        {
            _db = db;
            _config = config;
            _email = email;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            if (request == null)
                return BadRequest("Request body is required.");

            if (string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest("Username, email and password are required.");
            }

            var username = request.Username.Trim();
            var email = request.Email.Trim();

            var exists = await _db.Users.AnyAsync(u => u.Username == username || u.Email == email);
            if (exists)
                return Conflict("Username or email already exists.");

            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Role = UserRole.Staff
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Registered successfully" });
        }

        [HttpPost("login")]
        public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest request)
        {
            if (request == null)
                return BadRequest("Request body is required.");

            if (string.IsNullOrWhiteSpace(request.UsernameOrEmail) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest("Username/email and password are required.");

            var login = request.UsernameOrEmail.Trim();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login);
            if (user == null)
                return Unauthorized("Invalid credentials");

            if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Unauthorized("Invalid credentials");

            // If JWT config is missing, return a clean error instead of throwing a 500.
            if (!TryGenerateJwt(user, out var token, out var expiresAt, out var error))
                return StatusCode(500, error);

            return new LoginResponse
            {
                UserId = user.Id,
                Token = token!,
                ExpiresAt = expiresAt,
                Username = user.Username,
                Role = user.Role.ToString(),
                MustChangePassword = user.MustChangePassword
            };
        }

        [Authorize]
        [HttpGet("me")]
        public IActionResult Me()
        {
            // This will work reliably if you include JwtRegisteredClaimNames.UniqueName (see token generation below)
            var username = User.Identity?.Name ?? "";
            var role = User.FindFirstValue(ClaimTypes.Role) ?? "";

            // Also return the user id for debugging / client needs
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

            return Ok(new
            {
                UserId = userId,
                Username = username,
                Role = role
            });
        }

        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
                return BadRequest("New password must be at least 8 characters.");

            var userId = User.GetUserId();
            var user = await _db.Users.FindAsync(userId);
            if (user == null) return NotFound();

            if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
                return Unauthorized("Current password is incorrect.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            user.MustChangePassword = false;
            await _db.SaveChangesAsync();

            return NoContent();
        }

        /// <summary>
        /// Sends a password-reset link to the user's email address.
        /// Always returns 200 (to prevent user enumeration).
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Email))
                return BadRequest("Email is required.");

            var email = request.Email.Trim();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);

            if (user == null)
                return NotFound(new { message = "No account found with that email address." });

            // Generate a secure token and store it (1-hour expiry)
            user.PasswordResetToken = Guid.NewGuid().ToString("N");
            user.PasswordResetTokenExpiry = DateTime.UtcNow.AddHours(1);
            await _db.SaveChangesAsync();

            // Build reset link using configured AppUrl (or fallback)
            var appUrl = _config["AppUrl"] ?? "https://main.d1imfsef8qotjc.amplifyapp.com";
            var resetLink = $"{appUrl.TrimEnd('/')}/?reset={user.PasswordResetToken}";

            await _email.SendPasswordResetAsync(user.Email, user.Username, resetLink);

            return Ok(new { message = "A password reset link has been sent to your email." });
        }

        /// <summary>
        /// Resets the user's password using a previously issued token.
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Token))
                return BadRequest("Reset token is required.");

            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
                return BadRequest("New password must be at least 8 characters.");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.PasswordResetToken == request.Token);

            if (user == null || user.PasswordResetTokenExpiry == null || user.PasswordResetTokenExpiry < DateTime.UtcNow)
                return BadRequest("This reset link is invalid or has expired.");

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            user.PasswordResetToken = null;
            user.PasswordResetTokenExpiry = null;
            user.MustChangePassword = false;
            await _db.SaveChangesAsync();

            return Ok(new { message = "Password reset successfully. You can now log in." });
        }

        private bool TryGenerateJwt(User user, out string? token, out DateTime expiresAt, out string? error)
        {
            token = null;
            error = null;

            var jwt = _config.GetSection("Jwt");

            var keyString = jwt["Key"];
            var issuer = jwt["Issuer"];
            var audience = jwt["Audience"];
            var expiresText = jwt["ExpiresMinutes"]; // e.g. "120"

            if (string.IsNullOrWhiteSpace(keyString) ||
                string.IsNullOrWhiteSpace(issuer) ||
                string.IsNullOrWhiteSpace(audience))
            {
                expiresAt = DateTime.UtcNow;
                error = "JWT is not configured on the server (Jwt:Key/Issuer/Audience).";
                return false;
            }

            double expiresMinutes = 120;
            if (!string.IsNullOrWhiteSpace(expiresText))
            {
                if (!double.TryParse(expiresText, NumberStyles.Float, CultureInfo.InvariantCulture, out expiresMinutes) || expiresMinutes <= 0)
                    expiresMinutes = 120;
            }

            expiresAt = DateTime.UtcNow.AddMinutes(expiresMinutes);

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyString));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),

                // IMPORTANT: ensures User.Identity.Name is populated by default JWT handler
                new(JwtRegisteredClaimNames.UniqueName, user.Username ?? string.Empty),

                new(ClaimTypes.Role, user.Role.ToString())
            };

            var jwtToken = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: expiresAt,
                signingCredentials: creds
            );

            token = new JwtSecurityTokenHandler().WriteToken(jwtToken);
            return true;
        }
    }
}
