using Chat.Api.Auth;
using Chat.Api.Data;
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

        public UsersController(ChatDbContext db)
        {
            _db = db;
        }

        // GET api/users/me
        [HttpGet("me")]
        public async Task<ActionResult<object>> GetMe()
        {
            var userId = User.GetUserId(); // 👈 uses Claims extension

            var user = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.Email,
                    Role = u.Role.ToString(), // enum -> "Admin"/"Staff"
                    u.CreatedAt
                })
                .FirstOrDefaultAsync();

            if (user == null)
                return NotFound();

            return Ok(user);
        }

        // GET api/users
        // List users (optionally filtered by ?search=), excluding current user
        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetUsers([FromQuery] string? search = null)
        {
            var currentUserId = User.GetUserId();

            var query = _db.Users
                .Where(u => u.Id != currentUserId); // exclude current user

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

    }
}
