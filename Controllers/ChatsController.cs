using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.Hubs;
using Chat.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

// Alias to disambiguate Chat type
using ChatEntity = Chat.Api.Models.Chat;

namespace Chat.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ChatsController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IHubContext<ChatHub> _chatHub;

        public ChatsController(ChatDbContext db, IHubContext<ChatHub> chatHub)
        {
            _db = db;
            _chatHub = chatHub;
        }

        // DTOs (nested records) ---------------------------------
        public record CreatePrivateChatRequest(Guid TargetUserId);
        public record CreateGroupChatRequest(string Name, List<Guid> MemberIds);
        public record SendMessageRequest(string? Text, List<Guid>? AttachmentIds = null, string? GifUrl = null);

        // Member management DTO
        public record AddMemberRequest(Guid UserId);


        // DTO for chat summaries
        public record ChatSummaryDto(
            Guid ChatId,
            string? Name,
            bool IsGroup,
            int UnreadCount
        );

        // GET api/chats
        // List chats that the current user belongs to
        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetMyChats()
        {
            var userId = User.GetUserId();

            var items = await _db.ChatMembers
                .Where(cm => cm.UserId == userId)
                .Include(cm => cm.Chat)
                .Select(cm => new
                {
                    cm.ChatId,
                    cm.Chat!.IsGroup,
                    ChatName = cm.Chat.Name,
                    cm.IsAdmin,
                    cm.JoinedAt,
                    cm.LastReadAt,

                    OtherUserName = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.Username)
                        .FirstOrDefault(),

                    // ✅ FIX: don't count your own messages as unread
                    UnreadCount = _db.Messages.Count(m =>
                        m.ChatId == cm.ChatId &&
                        m.SenderId != userId &&
                        (cm.LastReadAt == null || m.CreatedAt > cm.LastReadAt))
                })
                .ToListAsync();

            var result = items.Select(i => new
            {
                chatId = i.ChatId,
                isGroup = i.IsGroup,
                name = i.IsGroup
                    ? (i.ChatName ?? "Group chat")
                    : (i.OtherUserName ?? "Direct chat"),
                unreadCount = i.UnreadCount
            });

            return Ok(result);
        }


        // POST api/chats/private
        // Create or reuse a 1-to-1 chat between current user and target user
        [HttpPost("private")]
        public async Task<ActionResult<object>> CreatePrivateChat([FromBody] CreatePrivateChatRequest request)
        {
            var userId = User.GetUserId();
            var targetId = request.TargetUserId;

            if (userId == targetId)
                return BadRequest("Cannot create private chat with yourself");

            // Check if chat already exists between the two users
            // Check if chat already exists between the two users (MySQL-safe)
            var existingChatId = await (
                from cm in _db.ChatMembers
                join c in _db.Chats on cm.ChatId equals c.Id
                where cm.UserId == userId
                      && !c.IsGroup
                      && _db.ChatMembers.Any(cm2 => cm2.ChatId == cm.ChatId && cm2.UserId == targetId)
                select (Guid?)cm.ChatId
            ).FirstOrDefaultAsync();

            ChatEntity chat;

            if (existingChatId.HasValue)
            {
                chat = await _db.Chats.FindAsync(existingChatId.Value)
                       ?? throw new InvalidOperationException("Chat not found");
            }
            else
            {
                chat = new ChatEntity
                {
                    Id = Guid.NewGuid(),
                    IsGroup = false,
                    Name = null, // UI can show "other user's name"
                    CreatedByUserId = userId,
                    CreatedAt = DateTime.UtcNow
                };

                var members = new[]
                {
                    new ChatMember
                    {
                        Id = Guid.NewGuid(),
                        ChatId = chat.Id,
                        UserId = userId,
                        IsAdmin = true,
                        JoinedAt = DateTime.UtcNow
                    },
                    new ChatMember
                    {
                        Id = Guid.NewGuid(),
                        ChatId = chat.Id,
                        UserId = targetId,
                        IsAdmin = true,
                        JoinedAt = DateTime.UtcNow
                    }
                };

                _db.Chats.Add(chat);
                _db.ChatMembers.AddRange(members);
                await _db.SaveChangesAsync();
            }

            return Ok(new
            {
                chat.Id,
                chat.IsGroup,
                chat.Name,
                chat.CreatedByUserId,
                chat.CreatedAt
            });
        }

        // POST api/chats/group
        [HttpPost("group")]
        public async Task<ActionResult<object>> CreateGroupChat([FromBody] CreateGroupChatRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest("Group name is required");

            var userId = User.GetUserId();

            var chat = new ChatEntity
            {
                Id = Guid.NewGuid(),
                IsGroup = true,
                Name = request.Name,
                CreatedByUserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            var allMemberIds = (request.MemberIds ?? new List<Guid>()).Distinct().ToList();
            if (!allMemberIds.Contains(userId))
                allMemberIds.Add(userId);

            var members = allMemberIds.Select(id => new ChatMember
            {
                Id = Guid.NewGuid(),
                ChatId = chat.Id,
                UserId = id,
                IsAdmin = (id == userId),
                JoinedAt = DateTime.UtcNow
            }).ToList();

            _db.Chats.Add(chat);
            _db.ChatMembers.AddRange(members);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                chat.Id,
                chat.IsGroup,
                chat.Name,
                chat.CreatedByUserId,
                chat.CreatedAt
            });
        }

        // GET api/chats/{chatId}/members
        [HttpGet("{chatId:guid}/members")]
        public async Task<ActionResult<IEnumerable<object>>> GetMembers(Guid chatId)
        {
            var callerId = User.GetUserId();

            var isMember = await _db.ChatMembers.AnyAsync(cm => cm.ChatId == chatId && cm.UserId == callerId);
            if (!isMember)
                return Forbid();

            var members = await _db.ChatMembers
                .Where(cm => cm.ChatId == chatId)
                .Include(cm => cm.User)
                .OrderBy(cm => cm.JoinedAt)
                .Select(cm => new
                {
                    userId = cm.UserId,
                    username = cm.User != null ? cm.User.Username : null,
                    isAdmin = cm.IsAdmin,
                    joinedAt = cm.JoinedAt,
                    lastReadAt = cm.LastReadAt
                })
                .ToListAsync();

            return Ok(members);
        }

        // POST api/chats/{chatId}/members
        // Add a user to a group chat (admins only)
        [HttpPost("{chatId:guid}/members")]
        public async Task<ActionResult<object>> AddMember(Guid chatId, [FromBody] AddMemberRequest request)
        {
            var callerId = User.GetUserId();

            var chat = await _db.Chats
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == chatId);

            if (chat == null)
                return NotFound("Chat not found");

            if (!chat.IsGroup)
                return BadRequest("Cannot add members to a private chat");

            var callerMembership = chat.Members.FirstOrDefault(m => m.UserId == callerId);
            if (callerMembership == null || !callerMembership.IsAdmin)
                return Forbid();

            var targetUser = await _db.Users.FindAsync(request.UserId);
            if (targetUser == null)
                return NotFound("User not found");

            var alreadyMember = chat.Members.Any(m => m.UserId == request.UserId);
            if (alreadyMember)
                return BadRequest("User is already a member of the chat");

            var newMember = new ChatMember
            {
                Id = Guid.NewGuid(),
                ChatId = chat.Id,
                UserId = request.UserId,
                IsAdmin = false,
                JoinedAt = DateTime.UtcNow
            };

            _db.ChatMembers.Add(newMember);
            await _db.SaveChangesAsync();

            await _chatHub.Clients.Group(chatId.ToString()).SendAsync("MemberAdded", new
            {
                chatId = chat.Id,
                userId = targetUser.Id,
                username = targetUser.Username,
                joinedAt = newMember.JoinedAt
            });

            return Ok(new
            {
                userId = newMember.UserId,
                username = targetUser.Username,
                isAdmin = newMember.IsAdmin,
                joinedAt = newMember.JoinedAt
            });
        }

        // DELETE api/chats/{chatId}/members/{memberId}
        // Remove a user from a group chat. Admins can remove others; users can remove themselves.
        [HttpDelete("{chatId:guid}/members/{memberId:guid}")]
        public async Task<ActionResult> RemoveMember(Guid chatId, Guid memberId)
        {
            var callerId = User.GetUserId();

            var chat = await _db.Chats
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == chatId);

            if (chat == null)
                return NotFound("Chat not found");

            if (!chat.IsGroup)
                return BadRequest("Cannot remove members from a private chat");

            var membershipToRemove = chat.Members.FirstOrDefault(m => m.UserId == memberId);
            if (membershipToRemove == null)
                return NotFound("Member not found in chat");

            var callerMembership = chat.Members.FirstOrDefault(m => m.UserId == callerId);
            if (callerMembership == null)
                return Forbid();

            var isSelf = callerId == memberId;
            var isCallerAdmin = callerMembership.IsAdmin;

            if (!isSelf && !isCallerAdmin)
                return Forbid();

            // Prevent removing the last admin when removing another admin
            if (membershipToRemove.IsAdmin && !isSelf)
            {
                var otherAdminExists = chat.Members.Any(m => m.UserId != membershipToRemove.UserId && m.IsAdmin);
                if (!otherAdminExists)
                    return BadRequest("Cannot remove the last admin. Promote another admin first.");
            }

            _db.ChatMembers.Remove(membershipToRemove);
            await _db.SaveChangesAsync();

            var removedUser = await _db.Users.FindAsync(memberId);

            await _chatHub.Clients.Group(chatId.ToString()).SendAsync("MemberRemoved", new
            {
                chatId = chat.Id,
                userId = memberId,
                username = removedUser?.Username
            });

            return NoContent();
        }

        // GET api/chats/{chatId}/messages?skip=0&take=50
        [HttpGet("{chatId:guid}/messages")]
        public async Task<ActionResult<IEnumerable<object>>> GetMessages(
            Guid chatId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50)
        {
            var userId = User.GetUserId();

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember)
                return Forbid();

            if (take <= 0) take = 50;
            if (take > 200) take = 200;

            var messages = await _db.Messages
                .Where(m => m.ChatId == chatId)
                .Include(m => m.Sender) // <-- make sure Message has a Sender nav prop
                .OrderByDescending(m => m.CreatedAt)
                .Skip(skip)
                .Take(take)
                .Select(m => new
                {
                    m.Id,
                    m.ChatId,
                    m.SenderId,
                    SenderUserName = m.Sender.Username,
                    m.Text,
                    m.CreatedAt,
                    m.GifUrl
                })
                .ToListAsync();

            messages.Reverse(); // oldest first for UI

            return Ok(messages);
        }


        // POST api/chats/{chatId}/read
        [HttpPost("{chatId:guid}/read")]
        public async Task<ActionResult> MarkChatAsRead(Guid chatId)
        {
            var userId = User.GetUserId();

            var membership = await _db.ChatMembers
                .FirstOrDefaultAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (membership == null)
                return Forbid();

            // Set LastReadAt to "now"
            membership.LastReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return NoContent();
        }

        // POST api/chats/{chatId}/messages
        [HttpPost("{chatId:guid}/messages")]
        public async Task<ActionResult<object>> SendMessage(Guid chatId, [FromBody] SendMessageRequest request)
        {
            var userId = User.GetUserId();

            var text = request.Text?.Trim();
            var gifUrl = request.GifUrl?.Trim();

            var hasText = !string.IsNullOrWhiteSpace(text);
            var hasGif = !string.IsNullOrWhiteSpace(gifUrl);
            var hasAttachments = request.AttachmentIds?.Any() == true;

            if (!hasText && !hasGif && !hasAttachments)
                return BadRequest("Message text, attachments or gif url required");

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember)
                return Forbid();

            var message = new Message
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                SenderId = userId,
                Text = hasText ? text! : string.Empty,
                GifUrl = hasGif ? gifUrl : null,
                CreatedAt = DateTime.UtcNow
            };

            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            // Attach pre-uploaded attachments (if any)
            List<Attachment> attached = new();
            if (hasAttachments)
            {
                attached = await _db.Attachments
                    .Where(a => request.AttachmentIds!.Contains(a.Id) && a.MessageId == null)
                    .ToListAsync();

                foreach (var a in attached)
                    a.MessageId = message.Id;

                await _db.SaveChangesAsync();
            }

            var senderUserName = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync();

            var attachmentDtos = attached.Select(a => new
            {
                a.Id,
                a.FileName,
                a.ContentType,
                a.Url
            }).ToList();

            var dto = new
            {
                message.Id,
                message.ChatId,
                message.SenderId,
                SenderUserName = senderUserName,
                message.Text,
                message.CreatedAt,
                GifUrl = message.GifUrl,
                Attachments = attachmentDtos
            };

            // 1) Push the message to anyone viewing this chat
            await _chatHub.Clients.Group(chatId.ToString())
                .SendAsync("ReceiveMessage", dto);

            // 2) Notify chat members (except sender) to refresh sidebar / unread counts
            var memberIds = await _db.ChatMembers
                .Where(cm => cm.ChatId == chatId)
                .Select(cm => cm.UserId)
                .ToListAsync();

            foreach (var memberId in memberIds.Where(id => id != userId))
            {
                await _chatHub.Clients.User(memberId.ToString())
                    .SendAsync("ChatUpdated", new
                    {
                        chatId,
                        lastMessageAt = message.CreatedAt,
                        lastMessagePreview = hasText ? text : (hasGif ? "GIF" : "Attachment")
                    });
            }

            return Ok(dto);
        }





    }
}
