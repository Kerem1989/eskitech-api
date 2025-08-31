using System.Globalization;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var csvUrl = builder.Configuration["Sheet:CsvUrl"]!;
var http = new HttpClient();
List<Product> products = new();
DateTimeOffset lastReload = DateTimeOffset.UtcNow;

static async Task<string> GetVismaTokenAsync(HttpClient http, string clientId, string clientSecret)
{
    var body = new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["client_secret"] = clientSecret,
        ["grant_type"] = "client_credentials",
        ["scope"] = "ea:api"
    };
    using var req = new HttpRequestMessage(HttpMethod.Post,
        """https://identity.sandbox.vismaonline.com/connect/token""")
    {
        Content = new FormUrlEncodedContent(body)
    };

    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
    var json = await resp.Content.ReadAsStringAsync();
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    return doc.RootElement.GetProperty("access_token").GetString()!;
}

// Lägg denna HELPER överst i Program.cs (t.ex. direkt efter GetVismaTokenAsync)
static async Task CreateVismaArticleAsync(
    HttpClient http, string baseUrl, string token,
    string name, string articleNumber, decimal? unitPrice = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/articles");
    req.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    // Minsta möjliga payload för sandbox.
    var body = new Dictionary<string, object?>
    {
        ["name"] = name,
        ["articleNumber"] = articleNumber,
        ["unitPrice"] = unitPrice  // kan tas bort om din tenant kräver annat
    };

    req.Content = new StringContent(
        System.Text.Json.JsonSerializer.Serialize(body),
        System.Text.Encoding.UTF8,
        "application/json");

    using var resp = await http.SendAsync(req);
    if (!resp.IsSuccessStatusCode)
    {
        var txt = await resp.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"POST /articles failed: {(int)resp.StatusCode} {txt}");
    }
}


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

app.MapPost("/products/admin/push-to-visma", async () =>
{
    var baseUrl = Environment.GetEnvironmentVariable("VISMA_BASE_URL");
    var clientId = Environment.GetEnvironmentVariable("VISMA_CLIENT_ID");
    var clientSecret = Environment.GetEnvironmentVariable("VISMA_CLIENT_SECRET");
    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        return Results.BadRequest(new { error = "Missing VISMA_BASE_URL / VISMA_CLIENT_ID / VISMA_CLIENT_SECRET" });

    var token = await GetVismaTokenAsync(http, clientId, clientSecret);

    int ok = 0, fail = 0;
    foreach (var p in products)
    {
        try
        {
            await CreateVismaArticleAsync(http, baseUrl, token, p.Name, p.Sku, p.Price);
            ok++;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Push failed for SKU {Sku}", p.Sku);
            fail++;
        }
    }
    lastReload = DateTimeOffset.UtcNow;
    return Results.Ok(new { status = "done", created = ok, failed = fail });
});

var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
_ = Task.Run(async () =>
{
    while (await periodicTimer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
    {
        try { await ReloadAsync(); }
        catch {}
    }
});



app.Run();
