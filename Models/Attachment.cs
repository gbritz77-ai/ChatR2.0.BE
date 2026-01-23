namespace Chat.Api.Models
{
    public class Attachment
    {
        public Guid Id { get; set; }

        // Make MessageId nullable so attachments can be uploaded before a Message exists
        public Guid? MessageId { get; set; }
        public Message? Message { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;

        // For now store a URL; once on AWS you’ll swap to S3 pre-signed URLs
        public string Url { get; set; } = string.Empty;
    }
}