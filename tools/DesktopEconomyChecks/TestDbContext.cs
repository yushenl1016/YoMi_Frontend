namespace YoMi_Admin.Library.Models
{
    // The checks exercise the real store against a disposable SQL schema; no product entities are needed.
    public sealed class DBContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public DBContext(Microsoft.EntityFrameworkCore.DbContextOptions options) : base(options) { }
    }
}
