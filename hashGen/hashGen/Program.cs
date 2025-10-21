using System.Text.Json;
using hashGen.Models;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

// Rate limiting simples (opcional)
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                AutoReplenishment = true,
                QueueLimit = 0
            }));

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsync("Muitas requisições. Tente novamente mais tarde.", token);
    };
});

var app = builder.Build();

app.UseRateLimiter();
app.UseSwagger();
app.UseSwaggerUI();

// In-memory history (persistência apenas durante execução)
var history = new List<HashResponse>();

app.MapPost("/hashtags", async (HashRequest req, IHttpClientFactory httpFactory) =>
{
    // validações
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "Campo 'text' é obrigatório." });

    var count = req.Count <= 0 ? 10 : req.Count;
    if (count > 30) count = 30;

    var model = string.IsNullOrWhiteSpace(req.Model) ? "llama3.2:3b" : req.Model;

    // Monta prompt controlado para obter apenas JSON com N hashtags
    var prompt = $"""
    Gere exatamente {count} hashtags únicas, curtas e relevantes para o texto abaixo.
    Regras:
    - Cada hashtag deve começar com '#'
    - Sem espaços dentro da hashtag
    - Sem duplicatas
    - Retorne apenas um JSON válido no formato:
      {{ "hashtags": ["#exemplo1", "#exemplo2", ...] }}
    Texto: "{req.Text}"
    """;

    var client = httpFactory.CreateClient();

    var body = new
    {
        model = model,
        prompt = prompt,
        stream = false
    };

    HttpResponseMessage ollamaResp;
    try
    {
        ollamaResp = await client.PostAsJsonAsync("http://localhost:11434/api/generate", body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Não foi possível conectar ao Ollama.", detail = ex.Message });
    }

    if (!ollamaResp.IsSuccessStatusCode)
    {
        var text = await ollamaResp.Content.ReadAsStringAsync();
        return Results.BadRequest(new { error = "Ollama retornou erro.", status = (int)ollamaResp.StatusCode, detail = text });
    }

    var respText = await ollamaResp.Content.ReadAsStringAsync();

    // Tenta extrair JSON com "hashtags" do retorno do Ollama
    List<string> hashtags = new();
    try
    {
        // Tenta parsear a resposta inteira como JSON
        using var doc = JsonDocument.Parse(respText);

        // Alguns modelos retornam em "response" ou "choices" ou diretamente.
        // Tentamos vários caminhos:
        if (doc.RootElement.TryGetProperty("hashtags", out var arr))
        {
            hashtags = arr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        else if (doc.RootElement.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.String)
        {
            // se "response" for string contendo JSON, tenta parsear
            var maybeJson = respProp.GetString() ?? string.Empty;
            if (maybeJson.TrimStart().StartsWith("{") || maybeJson.TrimStart().StartsWith("["))
            {
                using var inner = JsonDocument.Parse(maybeJson);
                if (inner.RootElement.TryGetProperty("hashtags", out var innerArr))
                {
                    hashtags = innerArr.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
                }
            }
            else
            {
                // fallback: extrair hashtags por pattern
                hashtags = ExtractHashtagsFromText(maybeJson, count);
            }
        }
        else
        {
            // fallback geral: procurar por "hashtags" como string dentro do texto
            var all = doc.RootElement.ToString();
            hashtags = ExtractHashtagsFromText(all, count);
        }
    }
    catch
    {
        // parsing falhou -> tenta extrair hashtags por regex/texto
        hashtags = ExtractHashtagsFromText(respText, count);
    }

    // Clean: garantir que comecem com '#', sem espaços e sem duplicatas, e exatos N (ou até o máximo encontrado)
    var finalList = hashtags
        .Select(h => h.Trim())
        .Where(h => !string.IsNullOrWhiteSpace(h))
        .Select(h => h.StartsWith("#") ? h : "#" + h.Replace(" ", ""))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(count)
        .ToList();

    if (!finalList.Any())
        return Results.BadRequest(new { error = "Não foi possível extrair hashtags válidas da resposta do Ollama." });

    var result = new HashResponse
    {
        Model = model,
        Count = finalList.Count,
        Hashtags = finalList
    };

    history.Add(result);

    return Results.Ok(result);
})
.Produces<HashResponse>(200)
.Produces(400);

app.MapGet("/history", () => Results.Ok(history));

app.Run();

// ----- Helpers -----
static List<string> ExtractHashtagsFromText(string text, int max)
{
    if (string.IsNullOrWhiteSpace(text)) return new();

    // tenta extrair palavras que começam com '#'
    var list = new List<string>();
    var tokens = text.Split(new[] { ' ', '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
    foreach (var t in tokens)
    {
        var token = t.Trim().Trim('"', '\'');
        if (token.StartsWith("#"))
        {
            list.Add(new string(token.TakeWhile(c => !char.IsWhiteSpace(c)).ToArray()));
        }
    }

    // se não encontrou, tenta pegar palavras relevantes (fallback)
    if (list.Count == 0)
    {
        var words = text
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(punctuation))
            .Where(w => w.Length > 2)
            .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => "#" + g.Key)
            .Take(max)
            .ToList();

        return words;
    }

    return list.Take(max).ToList();
}

static char[] punctuation = new[] { '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']' };
