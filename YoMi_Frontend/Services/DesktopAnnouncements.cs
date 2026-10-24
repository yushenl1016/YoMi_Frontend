using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using YoMi_Admin.Library.Models;
using YoMi_Admin.Library.Models.Frontend;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopAnnouncements
    {
        public const string SettingKey = "desktop_pet_announcements";
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        private readonly DBContext _db;
        public DesktopAnnouncements(DBContext db) { _db = db; }
        public sealed class Input
        {
            public long Revision { get; set; } = -1;
            public string Title { get; set; }
            public string Body { get; set; }
            public bool Pinned { get; set; }
        }
        public sealed class Announcement
        {
            public long Id { get; set; }
            public string Title { get; set; }
            public string Body { get; set; }
            public bool Pinned { get; set; }
            public string Author { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
        public sealed class Document
        {
            public long Revision { get; set; }
            public long NextId { get; set; } = 1;
            public List<Announcement> Announcements { get; set; } = new List<Announcement>();
        }
        public sealed class AnnouncementResult
        {
            public bool Ok { get; set; } = true;
            public long Revision { get; set; }
            public bool CanEdit { get; set; }
            public IReadOnlyList<Announcement> Announcements { get; set; }
        }
        public sealed class ConflictException : Exception { }
        private static Document Read(string value) => value == null ? new Document() : JsonSerializer.Deserialize<Document>(value, Json) ?? throw new JsonException("Invalid announcement document.");
        private static AnnouncementResult Result(Document doc, bool canEdit) => new AnnouncementResult
        {
            Revision = doc.Revision, CanEdit = canEdit,
            Announcements = doc.Announcements.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CreatedAt).ToList()
        };
        public async Task<AnnouncementResult> ListAsync(int? memberId)
        {
            var canEdit = memberId.HasValue && await _db.FrontendMember.AsNoTracking().AnyAsync(x => x.Id == memberId.Value && x.Role == "admin");
            var value = await _db.FrontendSiteSetting.AsNoTracking().Where(x => x.Key == SettingKey).Select(x => x.Value).SingleOrDefaultAsync();
            return Result(Read(value), canEdit);
        }
        public async Task<AnnouncementResult> WriteAsync(int memberId, string action, long id, Input input)
        {
            // Serializable also protects the first insert and competing edits across website processes.
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var author = await _db.FrontendMember.AsNoTracking().SingleOrDefaultAsync(x => x.Id == memberId);
            if (author?.Role != "admin") throw new UnauthorizedAccessException();
            var setting = await _db.FrontendSiteSetting.SingleOrDefaultAsync(x => x.Key == SettingKey);
            var document = Read(setting?.Value);
            if (input == null || input.Revision < 0) throw new ArgumentException("請提供公告版本。");
            if (input.Revision != document.Revision) throw new ConflictException();
            if (action != "delete" && (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 80 || string.IsNullOrWhiteSpace(input.Body) || input.Body.Length > 2000))
                throw new ArgumentException("標題限 1–80 字、內文限 1–2000 字。");
            var now = DateTime.UtcNow;
            if (action == "create")
            {
                if (document.Announcements.Count >= 200) throw new ArgumentException("公告已達 200 則，請先刪除舊公告。");
                document.Announcements.Add(new Announcement { Id = document.NextId++, Title = input.Title.Trim(), Body = input.Body.Trim(), Pinned = input.Pinned, Author = author.Name, CreatedAt = now, UpdatedAt = now });
            }
            else
            {
                var post = document.Announcements.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
                if (action == "delete") document.Announcements.Remove(post);
                else if (action == "update") { post.Title = input.Title.Trim(); post.Body = input.Body.Trim(); post.Pinned = input.Pinned; post.UpdatedAt = now; }
                else throw new ArgumentException("無效的公告操作。");
            }
            document.Revision++;
            if (setting == null)
            {
                setting = new FrontendSiteSetting { Key = SettingKey, Label = "桌寵公告", Description = "桌寵與下載頁共用公告", SortOrder = 0 };
                _db.FrontendSiteSetting.Add(setting);
            }
            setting.Value = JsonSerializer.Serialize(document, Json); setting.UpdatedAt = now;
            await _db.SaveChangesAsync(); await transaction.CommitAsync();
            return Result(document, true);
        }
    }
}
