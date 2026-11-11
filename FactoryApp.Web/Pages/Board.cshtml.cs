using System.Security.Claims;
using System.Text.Encodings.Web;
using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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

    public async Task<IActionResult> OnPostSubmitTestRunAsync([FromForm] SubmitTestRunInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(input.Result))
        {
            return UnprocessableFragment("Invalid request.");
        }

        if (string.IsNullOrWhiteSpace(input.Station) || Array.IndexOf(Stations, input.Station) < 0)
        {
            return UnprocessableFragment("Unknown station.");
        }

        var operatorEmail = User.FindFirstValue(ClaimTypes.Email);
        var result = await builds.SubmitTestRunAsync(
            input.BuildId, input.Result, input.Station, operatorEmail ?? string.Empty, input.FailureCode, ct);

        if (result.Outcome == TransitionOutcome.NotFound)
        {
            return NotFound();
        }

        if (result.Outcome == TransitionOutcome.Invalid)
        {
            return UnprocessableFragment(result.Error ?? "Invalid request.");
        }

        var runningColumn = await LoadColumnAsync(BuildStatus.Running, ct);
        return new PartialViewResult
        {
            ViewName = "_Column",
            ViewData = new ViewDataDictionary<BoardColumn>(ViewData, runningColumn) { ["Oob"] = true },
        };
    }

    // Each column is capped at 200 cards on its own, so the page and a move response agree.
    // Flat subquery values: a nullable record from FirstOrDefault is not reliably null in EF.
    private async Task<BoardColumn> LoadColumnAsync(BuildStatus status, CancellationToken ct)
    {
        var rows = await db.Builds
            .AsNoTracking()
            .Where(b => b.Status == status)
            .OrderByDescending(b => b.UpdatedAt)
            .ThenBy(b => b.Name)
            .ThenBy(b => b.Id)
            .Take(200)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Status,
                b.UpdatedAt,
                LatestStatus = b.TestRuns
                    .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                    .Select(t => (TestStatus?)t.Status)
                    .FirstOrDefault(),
                LatestFailureCode = b.TestRuns
                    .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                    .Select(t => t.FailureCode)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var cards = rows
            .Select(r => new BoardCard(r.Id, r.Name, r.Status, r.UpdatedAt,
                r.LatestStatus is { } latest ? new TestSummary(latest, r.LatestFailureCode) : null))
            .ToList();

        return new BoardColumn(status, cards);
    }

    private static ContentResult UnprocessableFragment(string message) => new()
    {
        StatusCode = StatusCodes.Status422UnprocessableEntity,
        ContentType = "text/html",
        Content = $"<p class=\"error\" role=\"alert\">{HtmlEncoder.Default.Encode(message)}</p>",
    };

    public record BoardCard(Guid Id, string Name, BuildStatus Status, DateTime UpdatedAt, TestSummary? LatestTest = null);

    public record TestSummary(TestStatus Status, string? FailureCode);

    public record BoardColumn(BuildStatus Status, IReadOnlyList<BoardCard> Cards);

    public record MoveResult(BoardColumn Old, BoardColumn New);

    public class ChangeStatusInput
    {
        public Guid BuildId { get; set; }
        public BuildStatus NewStatus { get; set; }
        public string? Station { get; set; }
    }

    public class SubmitTestRunInput
    {
        public Guid BuildId { get; set; }
        public TestStatus Result { get; set; }
        public string? Station { get; set; }
        public string? FailureCode { get; set; }
    }
}
