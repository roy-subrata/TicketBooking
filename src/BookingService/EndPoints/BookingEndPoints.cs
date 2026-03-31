

using BookingService.Data;
using BookingService.Data.Entities;
using BookingService.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BookingService.EndPoints;

public static class BookingEndPoints
{
    public static void MapBookingEndPoints(this WebApplication app)
    {

        app.MapGet("/bookings", async (IReadBookingDbContextFactory readDbContextFactory) =>
        {
            await using var readScope = await readDbContextFactory.CreateAsync();

            // The endpoint asks for a read context, and the selector decides
            // which replica should serve that request.
            app.Logger.LogInformation("Fetching all bookings from {DatabaseTarget}...", readScope.Selection.Name);
            var bookings = await readScope.DbContext.Bookings.ToListAsync();
            return Results.Ok(bookings);
        });

        app.MapPost("/bookings", async (CreateNewSeatRequest request, WriteBookingDbContext context) =>
        {
            app.Logger.LogInformation("Attempting to create a new seat {SeatNumber}...", request.SeatNumber);
            var existingBooking = await context.Bookings
                .FirstOrDefaultAsync(b => b.SeatNumber == request.SeatNumber);

            if (existingBooking != null)
            {
                return Results.BadRequest($"Seat {request.SeatNumber} already exists.");
            }

            var newBooking = new Booking
            {
                SeatNumber = request.SeatNumber,
                Status = BookingStatus.Available,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await context.Bookings.AddAsync(newBooking);

            await context.SaveChangesAsync();
            return Results.Ok("Booking created!");
        });

        app.MapPost("/book-optimistic", async (BookingRequest request, WriteBookingDbContext context) =>
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
        
        app.MapPost("/book-pessimistic", async (BookingRequest request, WriteBookingDbContext context) =>
        {

            using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            try
            {
                var booking = await context.Bookings
                    .FromSqlInterpolated($"""
                        SELECT * FROM "Bookings"
                        WHERE "SeatNumber" = {request.SeatNumber}
                        FOR UPDATE
                        """)
                    .FirstOrDefaultAsync();

                if (booking == null || booking.Status != BookingStatus.Available)
                {
                    await transaction.RollbackAsync();
                    return Results.BadRequest($"Seat {request.SeatNumber} is not available.");
                }

                booking.Status = BookingStatus.Booked;
                booking.UserId = request.UserId;
                booking.UpdatedAt = DateTime.UtcNow;

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return Results.Ok("Booking created!");
            }

            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return Results.BadRequest($"An error occurred: {ex.Message}");
            }


        });
    }


}

public record BookingRequest(int SeatNumber, string UserId);

public record CreateNewSeatRequest(int SeatNumber);
