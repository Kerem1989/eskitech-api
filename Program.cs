using System.Globalization;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var csvUrl = builder.Configuration["Sheet:CsvUrl"]!;
var http = new HttpClient();
List<Product> products = new();
DateTimeOffset lastReload = DateTimeOffset.UtcNow;


static List<Product> ParseCsv(string csv)
{
    var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    var list = new List<Product>();
    for (int i = 1; i < lines.Length; i++) 
    {
        var cols = lines[i].Trim().Split(',', StringSplitOptions.TrimEntries);
        if (cols.Length < 5) continue;
        list.Add(new Product(
            int.Parse(cols[0]),
            cols[1],
            cols[2],
            decimal.Parse(cols[3], CultureInfo.InvariantCulture),
            int.Parse(cols[4])
        ));
    }
    return list;
}

async Task ReloadAsync()
{
    var csv = await http.GetStringAsync(csvUrl);
    products = ParseCsv(csv);
    lastReload = DateTimeOffset.UtcNow;
}

await ReloadAsync();

app.MapGet("/products", () => products);
app.MapGet("/products/{id:int}", (int id) =>
{
    var p = products.FirstOrDefault(x => x.Id == id);
    return p is null ? Results.NotFound() : Results.Ok(p);
});
app.MapGet("/products/search", (string q) =>
{
    var s = q.Trim().ToLowerInvariant();
    var res = products.Where(p =>
        p.Name.ToLowerInvariant().Contains(s) ||
        p.Sku.ToLowerInvariant().Contains(s));
    return Results.Ok(res);
});
app.MapPost("/products/admin/reload", async () =>
{
    await ReloadAsync();
    return Results.Ok(new { status = "reloaded", count = products.Count });

});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    products = products.Count,
    lastReload
}));

var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
_ = Task.Run(async () =>
{
    while (await periodicTimer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
    {
        try { await ReloadAsync(); }
        catch { /* håll det minimalt: ignorera fel i PoC */ }
    }
});



app.Run();
