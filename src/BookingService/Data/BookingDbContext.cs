using BookingService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Data;

public class BookingDbContext : DbContext
{
    public BookingDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<Booking> Bookings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BookingDbContext).Assembly);
    }
}

public class WriteBookingDbContext : BookingDbContext
{
    public WriteBookingDbContext(DbContextOptions<WriteBookingDbContext> options) : base(options)
    {
    }
}

public class ReadBookingDbContext : BookingDbContext
{
    public ReadBookingDbContext(DbContextOptions<ReadBookingDbContext> options) : base(options)
    {
    }
}
