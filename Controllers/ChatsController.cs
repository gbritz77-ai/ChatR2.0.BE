using Amazon.S3;
using Amazon.S3.Model;
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
        private readonly IAmazonS3 _s3;
        private readonly IConfiguration _config;

        public ChatsController(ChatDbContext db, IHubContext<ChatHub> chatHub, IAmazonS3 s3, IConfiguration config)
        {
            _db = db;
            _chatHub = chatHub;
            _s3 = s3;
            _config = config;
        }

        private string? GetBucketName() =>
            _config["AWS:S3Bucket"] ?? _config["AWS__S3Bucket"];

        private string? GetPresignedGetUrl(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var bucket = GetBucketName();
            if (string.IsNullOrWhiteSpace(bucket)) return null;
            return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddHours(1)
            });
        }

        // DTOs (nested records) ---------------------------------
        public record CreatePrivateChatRequest(Guid TargetUserId);
        public record CreateGroupChatRequest(string Name, List<Guid> MemberIds, string? AvatarKey = null);
        public record SendMessageRequest(string? Text, List<Guid>? AttachmentIds = null, Guid? AttachmentId = null, string? GifUrl = null, Guid? ReplyToMessageId = null);
        public record AttachmentDto(Guid Id, string FileName, string ContentType, string Url);


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
                // Exclude direct chats where the other participant is the Master user
                .Where(cm => cm.Chat!.IsGroup || !_db.ChatMembers
                    .Any(x => x.ChatId == cm.ChatId && x.UserId != userId && x.User!.Role == UserRole.Master))
                .Include(cm => cm.Chat)
                .Select(cm => new
                {
                    cm.ChatId,
                    cm.Chat!.IsGroup,
                    ChatName = cm.Chat.Name,
                    CreatedByUserId = cm.Chat.CreatedByUserId,
                    cm.IsAdmin,
                    cm.JoinedAt,
                    cm.LastReadAt,

                    OtherUserName = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.Username)
                        .FirstOrDefault(),

                    OtherUserId = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => (Guid?)x.UserId)
                        .FirstOrDefault(),

                    OtherUserLastSeenAt = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.LastSeenAt)
                        .FirstOrDefault(),

                    OtherUserAvailabilityDays = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.AvailabilityDays)
                        .FirstOrDefault(),

                    OtherUserAvailabilityFrom = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.AvailabilityFrom)
                        .FirstOrDefault(),

                    OtherUserAvailabilityTo = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.AvailabilityTo)
                        .FirstOrDefault(),

                    OtherUserHasAvatar = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.AvatarKey != null)
                        .FirstOrDefault(),

                    OtherUserGroup = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.User!.Group)
                        .FirstOrDefault(),

                    OtherMemberLastReadAt = _db.ChatMembers
                        .Where(x => x.ChatId == cm.ChatId && x.UserId != userId)
                        .Select(x => x.LastReadAt)
                        .FirstOrDefault(),

                    ChatAvatarKey = cm.Chat!.AvatarKey,

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
                unreadCount = i.UnreadCount,
                createdByUserId = i.CreatedByUserId,
                otherUserId = i.OtherUserId,
                otherUserLastSeenAt = i.OtherUserLastSeenAt,
                otherUserAvailabilityDays = i.OtherUserAvailabilityDays,
                otherUserAvailabilityFrom = i.OtherUserAvailabilityFrom,
                otherUserAvailabilityTo   = i.OtherUserAvailabilityTo,
                otherUserHasAvatar        = i.OtherUserHasAvatar,
                otherUserGroup            = i.OtherUserGroup,
                otherMemberLastReadAt     = i.OtherMemberLastReadAt,
                chatAvatarUrl             = GetPresignedGetUrl(i.ChatAvatarKey),
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

                // Notify the other user so their sidebar updates immediately
                await _chatHub.Clients.User(targetId.ToString())
                    .SendAsync("NewChatCreated", new { chatId = chat.Id });
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

        // GET api/chats/group-avatar-upload-url?contentType=image/jpeg
        [HttpGet("group-avatar-upload-url")]
        public ActionResult<object> GetGroupAvatarUploadUrl([FromQuery] string contentType = "image/jpeg")
        {
            var bucket = GetBucketName();
            if (string.IsNullOrWhiteSpace(bucket))
                return StatusCode(500, "S3 bucket not configured");

            var key = $"group-avatars/{Guid.NewGuid()}";
            var expiresAt = DateTime.UtcNow.AddMinutes(10);

            var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = expiresAt,
                ContentType = contentType
            });

            return Ok(new { uploadUrl = url, key, expiresAt });
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
                CreatedAt = DateTime.UtcNow,
                AvatarKey = string.IsNullOrWhiteSpace(request.AvatarKey) ? null : request.AvatarKey.Trim()
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

        // DELETE api/chats/{chatId} — creator only, deletes the entire group
        [HttpDelete("{chatId:guid}")]
        public async Task<ActionResult> DeleteGroup(Guid chatId)
        {
            var userId = User.GetUserId();

            var chat = await _db.Chats
                .Include(c => c.Members)
                .FirstOrDefaultAsync(c => c.Id == chatId);

            if (chat == null) return NotFound();
            if (!chat.IsGroup) return BadRequest("Only group chats can be deleted.");
            if (chat.CreatedByUserId != userId) return Forbid();

            // Notify all members before deleting
            await _chatHub.Clients.Group(chatId.ToString()).SendAsync("GroupDeleted", new { chatId });

            // Remove all attachments, messages, members, then the chat
            var messageIds = await _db.Messages.Where(m => m.ChatId == chatId).Select(m => m.Id).ToListAsync();
            await _db.Attachments.Where(a => a.MessageId != null && messageIds.Contains(a.MessageId.Value)).ExecuteDeleteAsync();
            await _db.Messages.Where(m => m.ChatId == chatId).ExecuteDeleteAsync();
            await _db.ChatMembers.Where(cm => cm.ChatId == chatId).ExecuteDeleteAsync();
            _db.Chats.Remove(chat);
            await _db.SaveChangesAsync();

            return NoContent();
        }

        // PUT api/chats/{chatId}/avatar — update group avatar (creator only)
        [HttpPut("{chatId:guid}/avatar")]
        public async Task<ActionResult> UpdateGroupAvatar(Guid chatId, [FromBody] UpdateGroupAvatarRequest request)
        {
            var userId = User.GetUserId();

            var chat = await _db.Chats.FirstOrDefaultAsync(c => c.Id == chatId);
            if (chat == null) return NotFound();
            if (!chat.IsGroup) return BadRequest("Only group chats have avatars.");
            if (chat.CreatedByUserId != userId) return Forbid();

            chat.AvatarKey = request.AvatarKey?.Trim();
            await _db.SaveChangesAsync();

            var bucket = GetBucketName();
            string? avatarUrl = null;
            if (!string.IsNullOrWhiteSpace(chat.AvatarKey) && !string.IsNullOrWhiteSpace(bucket))
            {
                avatarUrl = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = chat.AvatarKey,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.UtcNow.AddHours(2)
                });
            }

            await _chatHub.Clients.Group(chatId.ToString()).SendAsync("GroupAvatarUpdated", new { chatId, avatarUrl });

            return Ok(new { avatarUrl });
        }

        [HttpGet("{chatId:guid}/messages")]
        public async Task<ActionResult<IEnumerable<object>>> GetMessages(Guid chatId, [FromQuery] int skip = 0, [FromQuery] int take = 50)
        {
            var userId = User.GetUserId();

            var isMember = await _db.ChatMembers.AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
            if (!isMember) return Forbid();

            if (take <= 0) take = 50;
            if (take > 200) take = 200;

            var msgs = await _db.Messages
                .Where(m => m.ChatId == chatId)
                .Include(m => m.Sender)
                .Include(m => m.ReplyToMessage).ThenInclude(r => r!.Sender)
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
                    m.GifUrl,
                    m.IsEdited,
                    m.EditedAt,
                    m.ReplyToMessageId,
                    ReplyToSenderName = m.ReplyToMessage != null && m.ReplyToMessage.Sender != null ? m.ReplyToMessage.Sender.Username : null,
                    ReplyToText = m.ReplyToMessage != null ? m.ReplyToMessage.Text : null,
                })
                .ToListAsync();

            msgs.Reverse(); // oldest first

            var messageIds = msgs.Select(m => m.Id).ToList();

            var attachments = await _db.Attachments
    .Where(a => a.MessageId != null && messageIds.Contains(a.MessageId.Value))
    .Select(a => new
    {
        a.Id,
        a.MessageId,
        a.FileName,
        a.ContentType,
        a.Url
    })
    .ToListAsync();

            var attachmentsByMessage = attachments
                .GroupBy(a => a.MessageId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new AttachmentDto(x.Id, x.FileName, x.ContentType, x.Url)).ToList()
                );

            var result = msgs.Select(m => new
            {
                m.Id,
                m.ChatId,
                m.SenderId,
                m.SenderUserName,
                m.Text,
                m.CreatedAt,
                m.GifUrl,
                m.IsEdited,
                m.EditedAt,
                ReplyTo = m.ReplyToMessageId.HasValue ? new
                {
                    id = m.ReplyToMessageId.Value,
                    senderName = m.ReplyToSenderName,
                    text = m.ReplyToText
                } : null,
                Attachments = attachmentsByMessage.TryGetValue(m.Id, out var list)
                    ? list
                    : new List<AttachmentDto>()
            });

            return Ok(result);

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

            // Notify other members so their sent-message ticks update in real-time
            await _chatHub.Clients.Group(chatId.ToString())
                .SendAsync("ChatRead", new { chatId, userId, lastReadAt = membership.LastReadAt });

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
                CreatedAt = DateTime.UtcNow,
                ReplyToMessageId = request.ReplyToMessageId
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

            var attachmentDtos = attached
            .Select(a => new AttachmentDto(a.Id, a.FileName, a.ContentType, a.Url))
            .ToList();


            // Load reply-to preview if present
            object? replyTo = null;
            if (message.ReplyToMessageId.HasValue)
            {
                var replied = await _db.Messages
                    .Include(m => m.Sender)
                    .FirstOrDefaultAsync(m => m.Id == message.ReplyToMessageId.Value);
                if (replied != null)
                    replyTo = new { id = replied.Id, senderName = replied.Sender?.Username, text = replied.Text };
            }

            var dto = new
            {
                message.Id,
                message.ChatId,
                message.SenderId,
                SenderUserName = senderUserName,
                message.Text,
                message.CreatedAt,
                GifUrl = message.GifUrl,
                Attachments = attachmentDtos,
                ReplyTo = replyTo
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

        // PUT api/chats/{chatId}/messages/{messageId}
        [HttpPut("{chatId:guid}/messages/{messageId:guid}")]
        public async Task<IActionResult> EditMessage(Guid chatId, Guid messageId, [FromBody] EditMessageRequest request)
        {
            var userId = User.GetUserId();

            var message = await _db.Messages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.ChatId == chatId);

            if (message == null) return NotFound();
            if (message.SenderId != userId) return Forbid();
            if (string.IsNullOrWhiteSpace(request.Text)) return BadRequest("Text cannot be empty.");

            message.Text = request.Text.Trim();
            message.IsEdited = true;
            message.EditedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _chatHub.Clients.Group(chatId.ToString()).SendAsync("MessageEdited", new
            {
                messageId,
                chatId,
                text = message.Text,
                editedAt = message.EditedAt
            });

            return Ok(new { message.Id, message.Text, message.IsEdited, message.EditedAt });
        }

        public record EditMessageRequest(string Text);
        public record UpdateGroupAvatarRequest(string? AvatarKey);

        // DELETE api/chats/{chatId}/messages/{messageId}
        [HttpDelete("{chatId:guid}/messages/{messageId:guid}")]
        public async Task<IActionResult> DeleteMessage(Guid chatId, Guid messageId)
        {
            var userId = User.GetUserId();

            var message = await _db.Messages
                .FirstOrDefaultAsync(m => m.Id == messageId && m.ChatId == chatId);

            if (message == null) return NotFound();
            if (message.SenderId != userId) return Forbid();

            _db.Messages.Remove(message);
            await _db.SaveChangesAsync();

            await _chatHub.Clients.Group(chatId.ToString())
                .SendAsync("MessageDeleted", new { messageId, chatId });

            return NoContent();
        }

    }
}