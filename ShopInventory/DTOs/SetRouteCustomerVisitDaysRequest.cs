using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>Sets the weekdays a van calls on a shop.</summary>
public class SetRouteCustomerVisitDaysRequest
{
    /// <summary>
    /// The calling days, using <see cref="System.DayOfWeek"/>'s numbering (Sunday = 0). An empty
    /// list clears the schedule, which is a legitimate state meaning "not yet known".
    /// </summary>
    [Required]
    public List<DayOfWeek> VisitDays { get; set; } = [];
}
