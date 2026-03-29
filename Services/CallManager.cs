using System.Collections.Concurrent;

namespace Chat.Api.Services
{
    public class CallSession
    {
        public string CallId { get; set; } = "";
        public Guid CallerId { get; set; }
        public string CallerName { get; set; } = "";
        public Guid? ChatId { get; set; }
        public ConcurrentDictionary<Guid, string> Participants { get; set; } = new(); // userId -> connectionId
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CallManager
    {
        private readonly ConcurrentDictionary<string, CallSession> _calls = new();

        public CallSession CreateCall(Guid callerId, string callerName, Guid? chatId)
        {
            var session = new CallSession
            {
                CallId = Guid.NewGuid().ToString(),
                CallerId = callerId,
                CallerName = callerName,
                ChatId = chatId,
            };
            _calls[session.CallId] = session;
            return session;
        }

        public bool TryGetCall(string callId, out CallSession? session)
            => _calls.TryGetValue(callId, out session);

        public void AddParticipant(string callId, Guid userId, string connectionId)
        {
            if (_calls.TryGetValue(callId, out var session))
                session.Participants[userId] = connectionId;
        }

        public void RemoveParticipant(string callId, Guid userId)
        {
            if (_calls.TryGetValue(callId, out var session))
            {
                session.Participants.TryRemove(userId, out _);
                if (session.Participants.IsEmpty)
                    _calls.TryRemove(callId, out _);
            }
        }

        public void EndCall(string callId)
            => _calls.TryRemove(callId, out _);
    }
}
