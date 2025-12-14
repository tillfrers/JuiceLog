using JuiceLog.Entities;
using Microsoft.EntityFrameworkCore;

namespace JuiceLog.Persistence;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Energy> Energy { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        modelBuilder.Entity<Energy>(entity =>
        { 
            entity.Property(e => e.Date)
                .HasColumnType("timestamp with time zone")
                .IsRequired();
        });
    }
}
