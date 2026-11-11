using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Pages;

public class BoardModel(FactoryDbContext db) : PageModel
{
    public IReadOnlyList<BoardColumn> Columns { get; private set; } = [];

    // The 200 cap applies to all columns together, so a busy column can crowd out another.
    public async Task OnGetAsync(CancellationToken ct)
    {
        var cards = await db.Builds
            .AsNoTracking()
            .OrderByDescending(b => b.UpdatedAt)
            .ThenBy(b => b.Name)
            .Take(200)
            .Select(b => new BoardCard(b.Id, b.Name, b.Status, b.UpdatedAt))
            .ToListAsync(ct);

        Columns = Enum.GetValues<BuildStatus>()
            .Select(status => new BoardColumn(status, cards.Where(c => c.Status == status).ToList()))
            .ToList();
    }

    public record BoardCard(Guid Id, string Name, BuildStatus Status, DateTime UpdatedAt);

    public record BoardColumn(BuildStatus Status, IReadOnlyList<BoardCard> Cards);
}
