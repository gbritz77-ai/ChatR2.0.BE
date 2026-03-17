using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly ChatDbContext _db;

        public ChatHub(ChatDbContext db)
        {
            _db = db;
        }

        public override async Task OnConnectedAsync()
        {
            var userId = Context.User?.GetUserId();
            if (userId.HasValue)
            {
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

        // Notify all users who share a chat with this user
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
                await Clients.User(contactId.ToString()).SendAsync("UserPresenceChanged", payload);
        }

        // Called by client when opening a chat
        public async Task JoinChat(Guid chatId)
        {
            var principal = Context.User;

            if (principal == null)
                throw new HubException("Unauthenticated connection");

            var userId = principal.GetUserId();

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember)
                throw new HubException("Not a member of this chat");

            await Groups.AddToGroupAsync(Context.ConnectionId, chatId.ToString());
        }

        // Send a message to a chat
        public async Task SendMessage(Guid chatId, string text)
        {
            var principal = Context.User;

            if (principal == null)
                throw new HubException("Unauthenticated connection");

            if (string.IsNullOrWhiteSpace(text))
                return;

            var userId = principal.GetUserId();

            var isMember = await _db.ChatMembers
                .AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);

            if (!isMember)
                throw new HubException("Not a member of this chat");

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
    }
}
