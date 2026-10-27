using Microsoft.EntityFrameworkCore;

// Test-only database and session boundary: no production settings or credentials are loaded.
namespace YoMi_Admin.Library.Models.Frontend
{
    public class FrontendMember { public int Id { get; set; } public string Name { get; set; } public string Role { get; set; } }
    public class FrontendSiteSetting
    {
        public int Id { get; set; } public string Key { get; set; } public string Value { get; set; }
        public string Label { get; set; } public string Description { get; set; }
        public int SortOrder { get; set; } public DateTime UpdatedAt { get; set; }
    }
}
namespace YoMi_Admin.Library.Models
{
    public class DBContext : DbContext
    {
        public DBContext(DbContextOptions<DBContext> options) : base(options) { }
        public DbSet<Frontend.FrontendMember> FrontendMember { get; set; }
        public DbSet<Frontend.FrontendSiteSetting> FrontendSiteSetting { get; set; }
        protected override void OnModelCreating(ModelBuilder model) => model.Entity<Frontend.FrontendSiteSetting>().HasIndex(x => x.Key).IsUnique();
    }
}
namespace YoMi_Frontend.Services
{
    public class DesktopPetStore
    {
        private readonly YoMi_Admin.Library.Models.DBContext _db;
        private static readonly HashSet<System.Data.Common.DbConnection> OpenedConnections = new();
        public static int ActiveConnections { get { lock (OpenedConnections) return OpenedConnections.Count(c => c.State == System.Data.ConnectionState.Open); } }
        public DesktopPetStore(YoMi_Admin.Library.Models.DBContext db) { _db = db; }
        private async Task Open()
        {
            await _db.Database.OpenConnectionAsync();
            lock (OpenedConnections) OpenedConnections.Add(_db.Database.GetDbConnection());
        }
        public static readonly HashSet<int> Revoked = new();
        public static int HoldNextMember;
        public static TaskCompletionSource<bool> DocumentsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static TaskCompletionSource<bool> DocumentsRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<int?> Member(string token)
        {
            await Open();
            return token?.StartsWith("fixture-") == true && int.TryParse(token.Substring(8), out var id) && id > 0 && !Revoked.Contains(id) ? id : null;
        }
        public async Task<string> MemberName(int member)
        {
            await Open();
            return await _db.FrontendMember.Where(x=>x.Id==member).Select(x=>x.Name).SingleOrDefaultAsync() ?? "會員";
        }
        public async Task<Dictionary<string, object>> Documents(int member)
        {
            await Open();
            if (Interlocked.CompareExchange(ref HoldNextMember, 0, member) == member)
            { DocumentsEntered.TrySetResult(true); await DocumentsRelease.Task; }
            return new Dictionary<string, object>
            {
                ["profile"] = new { payload = new { inventory = new[] { "hw-cat-ears", "face-happy", "background-beach", "decor-plant", "decor-seashell", "neon-lamp" } }, revision = 1 }
            };
        }
    }
}
