

namespace BookingService.Data.Entities;

public class Booking
{
    public int Id { get; set; }
    public int SeatNumber { get; set; }
    public  string? UserId { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime BookedAt { get; set; }

}

public enum BookingStatus
{
    Available,
    Booked
}