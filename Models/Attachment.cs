namespace Chat.Api.Models
{
    public class Attachment
    {
        public Guid Id { get; set; }
        public Guid ChatId { get; set; }
        public Guid? MessageId { get; set; }
        public Message? Message { get; set; }
        public string FileName { get; set; } = default!;
        public string ContentType { get; set; } = default!;
        public string Url { get; set; } = default!;
    }
}