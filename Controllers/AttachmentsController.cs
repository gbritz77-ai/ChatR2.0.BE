//csharp Controllers/AttachmentsController.cs
using Chat.Api.Auth;
using Chat.Api.Data;
using Chat.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AttachmentsController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly ChatDbContext _db;

        public AttachmentsController(IWebHostEnvironment env, ChatDbContext db)
        {
            _env = env;
            _db = db;
        }

        // Form model required by Swashbuckle for multipart/form-data
        public class UploadAttachmentRequest
        {
            public IFormFile? File { get; set; }
            public Guid? ChatId { get; set; }
        }

        // POST api/attachments
        // Form: file (IFormFile), optional chatId (Guid) to validate membership
        [HttpPost]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult<object>> Upload([FromForm] UploadAttachmentRequest model)
        {
            var file = model?.File;
            var chatId = model?.ChatId;

            if (file == null || file.Length == 0)
                return BadRequest("File is required");

            // basic validation: size and content-type whitelist
            const long maxBytes = 20 * 1024 * 1024; // 20 MB
            if (file.Length > maxBytes)
                return BadRequest("File too large");

            var allowed = new[]
            {
                "image/png","image/jpeg","image/gif","image/webp",
                "video/mp4","video/quicktime","application/pdf"
            };
            if (!allowed.Contains(file.ContentType))
                return BadRequest("Unsupported file type");

            var userId = User.GetUserId();

            if (chatId.HasValue)
            {
                var isMember = await _db.ChatMembers.AnyAsync(cm => cm.ChatId == chatId && cm.UserId == userId);
                if (!isMember)
                    return Forbid();
            }

            // Save to wwwroot/uploads for dev. Use blob storage (S3/Azure) in prod.
            var uploadsRoot = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads");
            Directory.CreateDirectory(uploadsRoot);

            var storedFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(uploadsRoot, storedFileName);

            await using (var fs = System.IO.File.Create(filePath))
            {
                await file.CopyToAsync(fs);
            }

            var url = $"{Request.Scheme}://{Request.Host}/uploads/{storedFileName}";

            var attachment = new Attachment
            {
                Id = Guid.NewGuid(),
                MessageId = null,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Url = url
            };

            _db.Attachments.Add(attachment);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.Url
            });
        }
    }
}