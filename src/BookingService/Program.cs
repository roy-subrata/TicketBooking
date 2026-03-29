using BookingService.Data;
using BookingService.Data.Entities;
using BookingService.EndPoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();


var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<BookingDbContext>(options =>
    options
    .UseSqlServer(connectionString)
    .UseSeeding((context, _) =>
    {
        var bookingDbSet = context.Set<Booking>();
        if (!bookingDbSet.Any())
        {
            for (int i = 1; i <= 100; i++)
            {
                var booking = new Booking
                {
                    SeatNumber = i,
                    Status = BookingStatus.Available,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                bookingDbSet.Add(booking);
            }
            context.SaveChanges();
        }

    })
    );

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}



app.UseHttpsRedirection();

app.MapGet("/live", () =>
{
    return Results.Ok("Booking Service is live!");
});
app.MapBookingEndPoints();
app.Run();
