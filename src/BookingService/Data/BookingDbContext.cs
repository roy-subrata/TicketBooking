

using BookingService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Data;


public class BookingDbContext : DbContext
{
    public BookingDbContext(DbContextOptions<BookingDbContext> options) : base(options)
    {
    }

    public DbSet<Booking> Bookings { get; set; }
}
