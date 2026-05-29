using Chat.Api.Auth;
using Chat.Api.Data;
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
    public class MeetingsController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IEmailService _email;
        private readonly IConfiguration _config;
        private readonly ILogger<MeetingsController> _logger;

        public MeetingsController(ChatDbContext db, IEmailService email, IConfiguration config, ILogger<MeetingsController> logger)
        {
            _db = db;
            _email = email;
            _config = config;
            _logger = logger;
        }

        public record CreateMeetingRequest(string Title, DateTime StartsAt, DateTime EndsAt, List<Guid> InviteeIds);
        public record RespondRequest(string Response); // "accepted" | "declined"

        // POST /api/meetings — create a meeting and invite users
        [HttpPost]
        public async Task<IActionResult> CreateMeeting([FromBody] CreateMeetingRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return BadRequest("Title is required.");
            if (req.StartsAt >= req.EndsAt)
                return BadRequest("End time must be after start time.");

            var userId = User.GetUserId();
            var organiser = await _db.Users.FindAsync(userId);
            if (organiser == null) return NotFound();

            var meeting = new Meeting
            {
                Id = Guid.NewGuid(),
                Title = req.Title.Trim(),
                StartsAt = req.StartsAt.ToUniversalTime(),
                EndsAt = req.EndsAt.ToUniversalTime(),
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow,
            };

            // Add invites (unique invitees only, skip self)
            var inviteeIds = (req.InviteeIds ?? new List<Guid>())
                .Distinct()
                .Where(id => id != userId)
                .ToList();

            var invitees = await _db.Users
                .Where(u => inviteeIds.Contains(u.Id))
                .ToListAsync();

            foreach (var invitee in invitees)
            {
                meeting.Invites.Add(new MeetingInvite
                {
                    Id = Guid.NewGuid(),
                    MeetingId = meeting.Id,
                    UserId = invitee.Id,
                    Status = MeetingInviteStatus.Pending,
                });
            }

            _db.Meetings.Add(meeting);
            await _db.SaveChangesAsync();

            // Send email invites (fire-and-forget errors to not fail the request)
            var appUrl = _config["AppUrl"] ?? "https://main.d1imfsef8qotjc.amplifyapp.com";
            foreach (var invitee in invitees)
            {
                try
                {
                    await _email.SendMeetingInviteAsync(
                        invitee.Email, invitee.Username, organiser.Username,
                        meeting.Title, meeting.StartsAt, meeting.EndsAt, appUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Meetings] Email failed for {Email}", invitee.Email);
                }
            }

            return Ok(new { id = meeting.Id, message = "Meeting created." });
        }

        // GET /api/meetings — get all meetings for current user (organiser + invited)
        [HttpGet]
        public async Task<IActionResult> GetMeetings()
        {
            var userId = User.GetUserId();

            var meetings = await _db.Meetings
                .Include(m => m.CreatedBy)
                .Include(m => m.Invites).ThenInclude(i => i.User)
                .Where(m => m.CreatedByUserId == userId || m.Invites.Any(i => i.UserId == userId))
                .OrderBy(m => m.StartsAt)
                .ToListAsync();

            var result = meetings.Select(m => MapMeeting(m, userId));
            return Ok(result);
        }

        // PUT /api/meetings/{id}/respond — accept or decline an invite
        [HttpPut("{id:guid}/respond")]
        public async Task<IActionResult> Respond(Guid id, [FromBody] RespondRequest req)
        {
            var userId = User.GetUserId();
            var invite = await _db.MeetingInvites
                .FirstOrDefaultAsync(i => i.MeetingId == id && i.UserId == userId);

            if (invite == null) return NotFound("No invite found for this meeting.");

            invite.Status = req.Response?.ToLower() == "accepted"
                ? MeetingInviteStatus.Accepted
                : MeetingInviteStatus.Declined;

            await _db.SaveChangesAsync();
            return NoContent();
        }

        // DELETE /api/meetings/{id} — delete (organiser only)
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteMeeting(Guid id)
        {
            var userId = User.GetUserId();
            var meeting = await _db.Meetings.FindAsync(id);
            if (meeting == null) return NotFound();
            if (meeting.CreatedByUserId != userId)
                return Forbid();

            _db.Meetings.Remove(meeting);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        private static object MapMeeting(Meeting m, Guid currentUserId)
        {
            var myInvite = m.Invites.FirstOrDefault(i => i.UserId == currentUserId);
            return new
            {
                id = m.Id,
                title = m.Title,
                startsAt = m.StartsAt,
                endsAt = m.EndsAt,
                createdByUserId = m.CreatedByUserId,
                createdByUsername = m.CreatedBy?.Username ?? "",
                createdAt = m.CreatedAt,
                isOrganiser = m.CreatedByUserId == currentUserId,
                myStatus = m.CreatedByUserId == currentUserId
                    ? "organiser"
                    : myInvite?.Status.ToString().ToLower() ?? "pending",
                invites = m.Invites.Select(i => new
                {
                    id = i.Id,
                    userId = i.UserId,
                    username = i.User?.Username ?? "",
                    status = i.Status.ToString().ToLower(),
                }),
            };
        }
    }
}
