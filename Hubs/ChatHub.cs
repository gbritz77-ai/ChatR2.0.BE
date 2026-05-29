using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.Models;
using Chat.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly ChatDbContext _db;
        private readonly CallManager _calls;

        public ChatHub(ChatDbContext db, CallManager calls)
        {
            _db = db;
            _calls = calls;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.GetUserId();
            if (userId.HasValue)
            {
                // Add to personal group so we can target this user reliably
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId.Value}");

                var user = await _db.Users.FindAsync(userId.Value);
                if (user != null)
                {
                    user.LastSeenAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    await BroadcastPresence(userId.Value, true);
                }
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var userId = Context.User?.GetUserId();
            if (userId.HasValue)
            {
                var user = await _db.Users.FindAsync(userId.Value);
                if (user != null)
                {
                    user.LastSeenAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    await BroadcastPresence(userId.Value, false);
                }
            }
            await base.OnDisconnectedAsync(exception);
        }

        public async Task PingPresence()
        {
            var userId = Context.User?.GetUserId();
            if (!userId.HasValue) return;

            var user = await _db.Users.FindAsync(userId.Value);
            if (user != null)
            {
                user.LastSeenAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await BroadcastPresence(userId.Value, true);
            }
        }

        private async Task BroadcastPresence(Guid userId, bool isOnline)
        {
            var contactIds = await _db.ChatMembers
                .Where(cm => _db.ChatMembers.Any(cm2 => cm2.ChatId == cm.ChatId && cm2.UserId == userId)
                             && cm.UserId != userId)
                .Select(cm => cm.UserId)
                .Distinct()
                .ToListAsync();

            var payload = new { userId, isOnline };

            foreach (var contactId in contactIds)
                await Clients.Group($"user_{contactId}").SendAsync("UserPresenceChanged", payload);
        }

        public async Task JoinChat(Guid chatId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember)
                throw new HubException("Not a member of this chat");

            await Groups.AddToGroupAsync(Context.ConnectionId, chatId.ToString());
        }

        public async Task SendMessage(Guid chatId, string text)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");

            if (string.IsNullOrWhiteSpace(text)) return;

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember) throw new HubException("Not a member of this chat");

            var message = new Message
            {
                Id = Guid.NewGuid(),
                ChatId = chatId,
                SenderId = userId,
                Text = text,
                CreatedAt = DateTime.UtcNow
            };

            _db.Messages.Add(message);
            await _db.SaveChangesAsync();

            await Clients.Group(chatId.ToString()).SendAsync("ReceiveMessage", new
            {
                message.Id,
                message.ChatId,
                message.SenderId,
                message.Text,
                message.CreatedAt
            });
        }

        // ─── Video Call Signaling ────────────────────────────────────────────────

        public async Task CallUser(string targetUserId, string? chatId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");
            var callerName = Context.User?.Identity?.Name ?? "Unknown";

            var chatGuid = chatId != null ? Guid.Parse(chatId) : (Guid?)null;
            var session = _calls.CreateCall(userId, callerName, chatGuid);
            _calls.AddParticipant(session.CallId, userId, Context.ConnectionId);

            await Groups.AddToGroupAsync(Context.ConnectionId, $"call_{session.CallId}");

            _calls.AddPendingInvitee(session.CallId, Guid.Parse(targetUserId));

            await Clients.Group($"user_{targetUserId}").SendAsync("IncomingCall", new
            {
                callId = session.CallId,
                callerId = userId.ToString(),
                callerName,
                chatId
            });

            // Tell the caller their callId so they can hang up
            await Clients.Caller.SendAsync("CallCreated", new { callId = session.CallId });
        }

        public async Task AcceptCall(string callId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");

            if (!_calls.TryGetCall(callId, out var session) || session == null)
                throw new HubException("Call not found or already ended");

            _calls.AddParticipant(callId, userId, Context.ConnectionId);
            _calls.RemovePendingInvitee(callId, userId);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"call_{callId}");

            var others = session.Participants
                .Where(p => p.Key != userId)
                .Select(p => new { userId = p.Key.ToString(), connectionId = p.Value })
                .ToList();

            await Clients.Caller.SendAsync("CallAccepted", new { callId, participants = others });

            await Clients.OthersInGroup($"call_{callId}").SendAsync("UserJoinedCall", new
            {
                callId,
                userId = userId.ToString(),
                connectionId = Context.ConnectionId
            });
        }

        public async Task RejectCall(string callId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");

            if (_calls.TryGetCall(callId, out var session) && session != null)
            {
                await Clients.Group($"user_{session.CallerId}").SendAsync("CallRejected", new
                {
                    callId,
                    userId = userId.ToString()
                });
                if (session.Participants.Count <= 1)
                    _calls.EndCall(callId);
            }
        }

        public async Task HangUp(string callId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");

            // Notify any pending invitees (still ringing) that the call ended
            var pendingInvitees = _calls.GetPendingInvitees(callId).ToList();

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"call_{callId}");
            _calls.RemoveParticipant(callId, userId);

            await Clients.Group($"call_{callId}").SendAsync("UserLeftCall", new
            {
                callId,
                userId = userId.ToString()
            });

            if (!_calls.TryGetCall(callId, out _))
            {
                await Clients.Group($"call_{callId}").SendAsync("CallEnded", new { callId });

                // Also notify pending invitees who never joined the call group
                foreach (var inviteeId in pendingInvitees)
                    await Clients.Group($"user_{inviteeId}").SendAsync("CallEnded", new { callId });
            }
        }

        public async Task CallGroup(string chatId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");
            var callerName = Context.User?.Identity?.Name ?? "Unknown";

            var chatGuid = Guid.Parse(chatId);

            var memberIds = await _db.ChatMembers
                .Where(m => m.ChatId == chatGuid && m.UserId != userId)
                .Select(m => m.UserId)
                .ToListAsync();

            if (!memberIds.Any())
                throw new HubException("No other members in this group");

            var session = _calls.CreateCall(userId, callerName, chatGuid);
            _calls.AddParticipant(session.CallId, userId, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, $"call_{session.CallId}");

            foreach (var memberId in memberIds)
                _calls.AddPendingInvitee(session.CallId, memberId);

            var groupName = await _db.Chats
                .Where(c => c.Id == chatGuid)
                .Select(c => c.Name)
                .FirstOrDefaultAsync() ?? "Group";

            foreach (var memberId in memberIds)
            {
                await Clients.Group($"user_{memberId}").SendAsync("IncomingCall", new
                {
                    callId = session.CallId,
                    callerId = userId.ToString(),
                    callerName,
                    chatId,
                    groupName
                });
            }

            await Clients.Caller.SendAsync("CallCreated", new { callId = session.CallId });
        }

        // ─── Meeting Call ────────────────────────────────────────────────────────
        // First participant creates the session and rings everyone else.
        // Subsequent participants join the existing session directly.
        public async Task JoinMeetingCall(string meetingId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");
            var callerName = Context.User?.Identity?.Name ?? "Unknown";

            var meetingGuid = Guid.Parse(meetingId);
            var meeting = await _db.Meetings
                .Include(m => m.Invites)
                .FirstOrDefaultAsync(m => m.Id == meetingGuid);

            if (meeting == null) throw new HubException("Meeting not found");

            bool isParticipant = meeting.CreatedByUserId == userId ||
                meeting.Invites.Any(i => i.UserId == userId);

            if (!isParticipant) throw new HubException("Not a participant of this meeting");

            var existingCall = _calls.GetCallByMeetingId(meetingGuid);

            if (existingCall != null)
            {
                // Join the already-running call
                _calls.AddParticipant(existingCall.CallId, userId, Context.ConnectionId);
                _calls.RemovePendingInvitee(existingCall.CallId, userId);
                await Groups.AddToGroupAsync(Context.ConnectionId, $"call_{existingCall.CallId}");

                var others = existingCall.Participants
                    .Where(p => p.Key != userId)
                    .Select(p => new { userId = p.Key.ToString(), connectionId = p.Value })
                    .ToList();

                await Clients.Caller.SendAsync("CallAccepted", new { callId = existingCall.CallId, participants = others });
                await Clients.OthersInGroup($"call_{existingCall.CallId}").SendAsync("UserJoinedCall", new
                {
                    callId = existingCall.CallId,
                    userId = userId.ToString(),
                    connectionId = Context.ConnectionId
                });
            }
            else
            {
                // First to join — create the session and ring everyone else
                var session = _calls.CreateCall(userId, callerName, null, meetingGuid);
                _calls.AddParticipant(session.CallId, userId, Context.ConnectionId);
                await Groups.AddToGroupAsync(Context.ConnectionId, $"call_{session.CallId}");

                var participantIds = new HashSet<Guid>();
                if (meeting.CreatedByUserId != userId)
                    participantIds.Add(meeting.CreatedByUserId);
                foreach (var invite in meeting.Invites)
                    if (invite.UserId != userId)
                        participantIds.Add(invite.UserId);

                foreach (var participantId in participantIds)
                {
                    _calls.AddPendingInvitee(session.CallId, participantId);
                    await Clients.Group($"user_{participantId}").SendAsync("IncomingCall", new
                    {
                        callId = session.CallId,
                        callerId = userId.ToString(),
                        callerName,
                        meetingTitle = meeting.Title,
                        isMeetingCall = true
                    });
                }

                await Clients.Caller.SendAsync("CallCreated", new { callId = session.CallId });
            }
        }

        public async Task InviteToCall(string callId, string targetUserId)
        {
            var userId = Context.User?.GetUserId() ?? throw new HubException("Unauthenticated");
            var callerName = Context.User?.Identity?.Name ?? "Unknown";

            if (!_calls.TryGetCall(callId, out _))
                throw new HubException("Call not found");

            _calls.AddPendingInvitee(callId, Guid.Parse(targetUserId));

            await Clients.Group($"user_{targetUserId}").SendAsync("IncomingCall", new
            {
                callId,
                callerId = userId.ToString(),
                callerName,
                isGroupInvite = true
            });
        }

        // ─── WebRTC Signaling Relay ──────────────────────────────────────────────

        public async Task SendOffer(string callId, string targetConnectionId, object offer)
        {
            await Clients.Client(targetConnectionId).SendAsync("ReceiveOffer", new
            {
                callId,
                fromConnectionId = Context.ConnectionId,
                offer
            });
        }

        public async Task SendAnswer(string callId, string targetConnectionId, object answer)
        {
            await Clients.Client(targetConnectionId).SendAsync("ReceiveAnswer", new
            {
                callId,
                fromConnectionId = Context.ConnectionId,
                answer
            });
        }

        public async Task SendIceCandidate(string callId, string targetConnectionId, object candidate)
        {
            await Clients.Client(targetConnectionId).SendAsync("ReceiveIceCandidate", new
            {
                callId,
                fromConnectionId = Context.ConnectionId,
                candidate
            });
        }
    }
}
