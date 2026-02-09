using Amazon.S3;
using Amazon.S3.Model;
using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AttachmentsController : ControllerBase
    {
        private readonly ChatDbContext _db;
        private readonly IAmazonS3 _s3;
        private readonly IConfiguration _config;
        private readonly ILogger<AttachmentsController> _logger;

        public AttachmentsController(ChatDbContext db, IAmazonS3 s3, IConfiguration config, ILogger<AttachmentsController> logger)
        {
            _db = db;
            _s3 = s3;
            _config = config;
            _logger = logger;
        }

        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/png","image/jpeg","image/gif","image/webp",
            "video/mp4","video/quicktime","application/pdf"
        };

        private const long MaxBytes = 20 * 1024 * 1024; // 20 MB

        public class PresignUploadRequest
        {
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long FileSize { get; set; }
            public Guid? ChatId { get; set; }
        }

        public class PresignUploadResponse
        {
            public Guid AttachmentId { get; set; }
            public string Key { get; set; } = default!;
            public string UploadUrl { get; set; } = default!;
            public DateTime ExpiresAt { get; set; }
            public string FileName { get; set; } = default!;
            public string ContentType { get; set; } = default!;
        }

        public class PresignDownloadRequest
        {
            public Guid AttachmentId { get; set; }
            public Guid? ChatId { get; set; }
        }

        private string? GetBucketName()
        {
            // Normal config key (AWS:S3Bucket) AND fallback for env mistakes (AWS__S3Bucket)
            return _config["AWS:S3Bucket"] ?? _config["AWS__S3Bucket"];
        }

        private static string SanitizeFileName(string fileName)
        {
            // Ensure no path traversal
            var safe = Path.GetFileName(fileName);

            // Replace chars that commonly cause messy URLs/keys
            foreach (var ch in Path.GetInvalidFileNameChars())
                safe = safe.Replace(ch, '_');

            // Also replace spaces to keep URLs neat (optional, but helpful)
            safe = safe.Replace(' ', '_');

            // Avoid empty
            return string.IsNullOrWhiteSpace(safe) ? "file" : safe;
        }

        [HttpPost("presign-upload")]
        public async Task<ActionResult<PresignUploadResponse>> PresignUpload([FromBody] PresignUploadRequest model)
        {
            try
            {
                if (model.ChatId is null)
                    return BadRequest("ChatId is required");

                if (string.IsNullOrWhiteSpace(model.FileName))
                    return BadRequest("FileName is required");

                if (model.FileSize <= 0)
                    return BadRequest("FileSize is required");

                if (model.FileSize > MaxBytes)
                    return BadRequest("File too large");

                var contentType = string.IsNullOrWhiteSpace(model.ContentType)
                    ? "application/octet-stream"
                    : model.ContentType.Trim();

                if (!AllowedContentTypes.Contains(contentType))
                    return BadRequest("Unsupported file type");

                var userId = User.GetUserId();

                var isMember = await _db.ChatMembers.AnyAsync(cm => cm.ChatId == model.ChatId && cm.UserId == userId);
                if (!isMember)
                    return Forbid();

                var bucket = GetBucketName();
                if (string.IsNullOrWhiteSpace(bucket))
                    return StatusCode(500, "S3 bucket is not configured (AWS:S3Bucket)");

                var safeFileName = SanitizeFileName(model.FileName);
                var attachmentId = Guid.NewGuid();

                var key = $"attachments/{model.ChatId}/{DateTime.UtcNow:yyyy/MM}/{attachmentId:N}-{safeFileName}";
                var expiresAt = DateTime.UtcNow.AddMinutes(10);

                var presignReq = new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.PUT,
                    Expires = expiresAt,
                    ContentType = contentType
                };

                var uploadUrl = _s3.GetPreSignedURL(presignReq);

                // Save metadata now (MessageId can be linked later)
                var attachment = new Attachment
                {
                    Id = attachmentId,
                    ChatId = model.ChatId.Value,
                    MessageId = null,
                    FileName = safeFileName,
                    ContentType = contentType,
                    Url = key // storing S3 key here
                };

                _db.Attachments.Add(attachment);
                await _db.SaveChangesAsync();

                return Ok(new PresignUploadResponse
                {
                    AttachmentId = attachment.Id,
                    Key = key,
                    UploadUrl = uploadUrl,
                    ExpiresAt = expiresAt,
                    FileName = attachment.FileName,
                    ContentType = attachment.ContentType
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "presign-upload failed. chatId={ChatId}", model?.ChatId);
                return Problem(
                    title: "presign-upload failed",
                    detail: ex.Message,
                    statusCode: 500
                );
            }
        }

        [HttpPost("presign-download")]
        public async Task<ActionResult<object>> PresignDownload([FromBody] PresignDownloadRequest model)
        {
            try
            {
                if (model.ChatId is null)
                    return BadRequest("ChatId is required");

                var userId = User.GetUserId();

                var isMember = await _db.ChatMembers.AnyAsync(cm => cm.ChatId == model.ChatId && cm.UserId == userId);
                if (!isMember)
                    return Forbid();

                var attachment = await _db.Attachments.FirstOrDefaultAsync(a => a.Id == model.AttachmentId);
                if (attachment == null)
                    return NotFound("Attachment not found");

                var bucket = GetBucketName();
                if (string.IsNullOrWhiteSpace(bucket))
                    return StatusCode(500, "S3 bucket is not configured (AWS:S3Bucket)");

                var key = attachment.Url;
                if (string.IsNullOrWhiteSpace(key))
                    return StatusCode(500, "Attachment key missing");

                // Optional: verify object exists before issuing a download URL
                try
                {
                    await _s3.GetObjectMetadataAsync(bucket, key);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return NotFound("Attachment file not found in storage");
                }

                var expiresAt = DateTime.UtcNow.AddMinutes(10);

                var presignReq = new GetPreSignedUrlRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Verb = HttpVerb.GET,
                    Expires = expiresAt
                };

                var downloadUrl = _s3.GetPreSignedURL(presignReq);

                return Ok(new
                {
                    attachment.Id,
                    attachment.FileName,
                    attachment.ContentType,
                    key,
                    downloadUrl,
                    expiresAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "presign-download failed. attachmentId={AttachmentId}", model?.AttachmentId);
                return Problem(
                    title: "presign-download failed",
                    detail: ex.Message,
                    statusCode: 500
                );
            }
        }
    }
}
