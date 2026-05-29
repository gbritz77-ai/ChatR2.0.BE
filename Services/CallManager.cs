using System.Collections.Concurrent;

namespace Chat.Api.Services
{
    public class CallSession
    {
        public string CallId { get; set; } = "";
        public Guid CallerId { get; set; }
        public string CallerName { get; set; } = "";
        public Guid? ChatId { get; set; }
        public Guid? MeetingId { get; set; }
        public ConcurrentDictionary<Guid, string> Participants { get; set; } = new(); // userId -> connectionId
        public ConcurrentDictionary<Guid, byte> PendingInvitees { get; set; } = new(); // users who are ringing but haven't answered
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class CallManager
    {
        private readonly ConcurrentDictionary<string, CallSession> _calls = new();

        public CallSession CreateCall(Guid callerId, string callerName, Guid? chatId, Guid? meetingId = null)
        {
            var session = new CallSession
            {
                CallId = Guid.NewGuid().ToString(),
                CallerId = callerId,
                CallerName = callerName,
                ChatId = chatId,
                MeetingId = meetingId,
            };
            _calls[session.CallId] = session;
            return session;
        }

        public CallSession? GetCallByMeetingId(Guid meetingId)
            => _calls.Values.FirstOrDefault(s => s.MeetingId == meetingId);

        public bool TryGetCall(string callId, out CallSession? session)
            => _calls.TryGetValue(callId, out session);

        public void AddParticipant(string callId, Guid userId, string connectionId)
        {
            if (_calls.TryGetValue(callId, out var session))
                session.Participants[userId] = connectionId;
        }

        public void AddPendingInvitee(string callId, Guid userId)
        {
            if (_calls.TryGetValue(callId, out var session))
                session.PendingInvitees[userId] = 0;
        }

        public void RemovePendingInvitee(string callId, Guid userId)
        {
            if (_calls.TryGetValue(callId, out var session))
                session.PendingInvitees.TryRemove(userId, out _);
        }

        public IEnumerable<Guid> GetPendingInvitees(string callId)
        {
            if (_calls.TryGetValue(callId, out var session))
                return session.PendingInvitees.Keys.ToList();
            return Enumerable.Empty<Guid>();
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
