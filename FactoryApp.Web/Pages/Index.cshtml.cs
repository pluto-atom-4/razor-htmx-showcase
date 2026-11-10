using FactoryApp.Domain;
using FactoryApp.Domain.Entities;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FactoryApp.Web.Pages;

public class IndexModel(FactoryDbContext db) : PageModel
{
    public IReadOnlyList<BuildRow> Builds { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Builds = await db.Builds
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Name)
            .Take(100)
            .Select(b => new BuildRow(b.Id, b.Name, b.Status))
            .ToListAsync(ct);
    }

    public record BuildRow(Guid Id, string Name, BuildStatus Status);
}
