using System.Security.Claims;
using System.Text.Encodings.Web;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Pages;

public class BoardModel(FactoryDbContext db, BuildService builds) : PageModel
{
    public static readonly string[] Stations = ["ASSEMBLY-1", "TEST-BAY-1", "TEST-BAY-2"];

    public IReadOnlyList<BoardColumn> Columns { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var columns = new List<BoardColumn>();
        foreach (var status in Enum.GetValues<BuildStatus>())
        {
            columns.Add(await LoadColumnAsync(status, ct));
        }

        Columns = columns;
    }

    public async Task<IActionResult> OnPostChangeStatusAsync([FromForm] ChangeStatusInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(input.NewStatus))
        {
            return UnprocessableFragment("Invalid request.");
        }

        if (string.IsNullOrWhiteSpace(input.Station) || Array.IndexOf(Stations, input.Station) < 0)
        {
            return UnprocessableFragment("Unknown station.");
        }

        var operatorEmail = User.FindFirstValue(ClaimTypes.Email);
        var result = await builds.ChangeStatusAsync(
            input.BuildId, input.NewStatus, input.Station, operatorEmail ?? string.Empty, ct);

        if (result.Outcome == TransitionOutcome.NotFound)
        {
            return NotFound();
        }

        if (result.Outcome == TransitionOutcome.Invalid)
        {
            return UnprocessableFragment(result.Error ?? "Invalid request.");
        }

        // Do not publish result.Event here; publishing must happen after commit.
        var oldColumn = await LoadColumnAsync(result.Event!.OldStatus, ct);
        var newColumn = await LoadColumnAsync(input.NewStatus, ct);
        return Partial("_MoveResult", new MoveResult(oldColumn, newColumn));
    }

    // Each column is capped at 200 cards on its own, so the page and a move response agree.
    private async Task<BoardColumn> LoadColumnAsync(BuildStatus status, CancellationToken ct)
    {
        var cards = await db.Builds
            .AsNoTracking()
            .Where(b => b.Status == status)
            .OrderByDescending(b => b.UpdatedAt)
            .ThenBy(b => b.Name)
            .ThenBy(b => b.Id)
            .Take(200)
            .Select(b => new BoardCard(b.Id, b.Name, b.Status, b.UpdatedAt))
            .ToListAsync(ct);

        return new BoardColumn(status, cards);
    }

    private static ContentResult UnprocessableFragment(string message) => new()
    {
        StatusCode = StatusCodes.Status422UnprocessableEntity,
        ContentType = "text/html",
        Content = $"<p class=\"error\" role=\"alert\">{HtmlEncoder.Default.Encode(message)}</p>",
    };

    public record BoardCard(Guid Id, string Name, BuildStatus Status, DateTime UpdatedAt);

    public record BoardColumn(BuildStatus Status, IReadOnlyList<BoardCard> Cards);

    public record MoveResult(BoardColumn Old, BoardColumn New);

    public class ChangeStatusInput
    {
        public Guid BuildId { get; set; }
        public BuildStatus NewStatus { get; set; }
        public string? Station { get; set; }
    }
}
