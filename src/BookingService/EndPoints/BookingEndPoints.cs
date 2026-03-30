

using BookingService.Data;
using BookingService.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BookingService.EndPoints;

public static class BookingEndPoints
{
    public static void MapBookingEndPoints(this WebApplication app)
    {

        app.MapGet("/bookings", async (BookingDbContext context) =>
        {
            app.Logger.LogInformation("Fetching all bookings...");
            var bookings = await context.Bookings.ToListAsync();
            return Results.Ok(bookings);
        });

        app.MapPost("/bookings", async (BookingRequest request, BookingDbContext context) =>
        {
            app.Logger.LogInformation("Attempting to book seat {SeatNumber} for user {UserId}...", request.SeatNumber, request.UserId);
            var seatIsAvailable = await context.Bookings
                .AnyAsync(b => b.SeatNumber == request.SeatNumber && b.Status == BookingStatus.Available);

            if (!seatIsAvailable)
            {
                return Results.BadRequest($"Seat {request.SeatNumber} is not available.");
            }

            var existingBooking = await context.Bookings
                .FirstOrDefaultAsync(b => b.SeatNumber == request.SeatNumber);

            if (existingBooking != null && existingBooking.Status == BookingStatus.Booked)
            {
                return Results.BadRequest($"Seat {request.SeatNumber} is already booked.");
            }

            existingBooking.Status = BookingStatus.Booked;
            existingBooking.UserId = request.UserId;
            existingBooking.UpdatedAt = DateTime.UtcNow;

            await context.SaveChangesAsync();
            return Results.Ok("Booking created!");
        });

        app.MapPost("/book-optimistic", async (BookingRequest request, BookingDbContext context) =>
        {
            var booking = await context.Bookings
                .FirstOrDefaultAsync(b => b.SeatNumber == request.SeatNumber);

            if (booking == null || booking.Status == BookingStatus.Booked)
            {
                return Results.BadRequest($"Seat {request.SeatNumber} is not available.");
            }

            booking.Status = BookingStatus.Booked;
            booking.UserId = request.UserId;
            booking.UpdatedAt = DateTime.UtcNow;

            try
            {
                await context.SaveChangesAsync();
                return Results.Ok("Booking created!");
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict($"Seat {request.SeatNumber} was just booked by someone else. Please try again.");
            }
        });
    }

}

public record BookingRequest(int SeatNumber, string UserId);