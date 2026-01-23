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

        // Called by client when opening a chat
        public async Task JoinChat(Guid chatId)
        {
            var principal = Context.User;

            if (principal == null)
                throw new HubException("Unauthenticated connection");

            var userId = principal.GetUserId();

            // Check user is a member of this chat
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
                return; // or throw if you want

            var userId = principal.GetUserId();

            // Check membership
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

            // Broadcast to all users in this chat group
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
