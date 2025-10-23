using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Hashtag Generator API",
        Version = "v1",
        Description = "gerando hashtags com o Ollama"
    });
});

// resgistro IHttpClientFactory
builder.Services.AddHttpClient("ollama", client =>
{
    client.BaseAddress = new Uri("http://localhost:11434/");
    client.Timeout = TimeSpan.FromMinutes(3); 
});

var app = builder.Build();

var hashtagHistory = new List<object>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(opt =>
    {
        opt.SwaggerEndpoint("/swagger/v1/swagger.json", "Hashtag Generator API V1");
        opt.RoutePrefix = "swagger";
    });
}

app.MapGet("/hashtags", () =>
{
    return Results.Ok(hashtagHistory);
})
.WithName("GetAllHashtags")
.WithTags("Hashtags")
.WithOpenApi();

app.MapPost("/hashtags", async (HashtagRequest req, IHttpClientFactory httpFactory) =>
{
    // padrão
    var count = req.Count ?? 10;
    
    if (count < 1 || count > 30)
        return Results.BadRequest(new { error = "O campo 'count' deve ser entre 1 e 30." });

    if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.Model))
        return Results.BadRequest(new { error = "Os campos 'text' e 'model' são obrigatórios." });

    // JSON Schema 
    var jsonSchema = new
    {
        type = "object",
        properties = new
        {
            model = new { type = "string" },
            count = new { type = "integer" },
            hashtags = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "model", "count", "hashtags" }
    };

    string prompt = $"""
Gere exatamente {count} hashtags em português para o tema abaixo.
Regras:
- Retorne apenas um JSON válido que siga o schema fornecido.
- Cada hashtag deve começar com '#' (cerquilha), não conter espaços e não ter duplicatas.
- Não responda com explicações adicionais — somente JSON.

Tema: {req.Text}

Formato esperado: {"{"}"model": "<modelo>", "count": <n>, "hashtags": ["#exemplo", ...]{"}"} 
""";

    var ollamaReq = new Dictionary<string, object?>
    {
        ["model"] = req.Model,
        ["prompt"] = prompt,
        ["stream"] = false,
        ["format"] = jsonSchema
    };

    try
    {
        var client = httpFactory.CreateClient("ollama");
        using var resp = await client.PostAsJsonAsync("api/generate", ollamaReq);

        if (!resp.IsSuccessStatusCode)
        {
            var errorText = await resp.Content.ReadAsStringAsync();
            return Results.Problem($"Falha ao consultar o Ollama: {resp.StatusCode}. {TruncateForLog(errorText)}", statusCode: 500);
        }

        // Ler como JSON genérico
        using var bodyStream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(bodyStream);

        JsonElement root = doc.RootElement;

        // Alguns wrappers do Ollama retornam um objeto com campo "response" contendo o JSON como string.
        // Tratamos ambos os casos: 1) body já é o objeto desejado 2) body.response é string com JSON.
        JsonElement structuredElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.String)
        {
            // parse string content
            try
            {
                structuredElement = JsonDocument.Parse(respProp.GetString()!).RootElement;
            }
            catch (Exception)
            {
                return Results.Problem("Ollama retornou 'response' que não é JSON válido.", statusCode: 500);
            }
        }
        else
        {
            structuredElement = root;
        }

        // tentativa de extrair hashtags
        if (!structuredElement.TryGetProperty("hashtags", out var hashtagsElem) || hashtagsElem.ValueKind != JsonValueKind.Array)
        {
            return Results.Problem("A resposta do Ollama não contém a propriedade 'hashtags' no formato esperado.", statusCode: 500);
        }

        var hashtags = hashtagsElem.EnumerateArray()
            .Select(x => (x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString()!)
                          .Trim()
                          .Replace(" ", "")) // removemos espaços só por segurança
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // caso o modelo retorne menos que o solicitado
        if (hashtags.Count < count)
        {
            var needed = count - hashtags.Count;
            var fallback = GenerateFallbackHashtags(req.Text, needed, hashtags);
            hashtags.AddRange(fallback);
            hashtags = hashtags.Distinct(StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        }
        else
        {
            // garantir exatamente N 
            hashtags = hashtags.Take(count).ToList();
        }

        //  começar com # e sem espaços
        if (hashtags.Any(h => !h.StartsWith("#")))
            hashtags = hashtags.Select(h => h.StartsWith("#") ? h : "#" + h).ToList();
        hashtags = hashtags.Select(h => h.Replace(" ", "")).ToList();

        var responseObj = new { model = req.Model, count = hashtags.Count, hashtags };
        hashtagHistory.Add(responseObj);
        return Results.Ok(responseObj);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"Erro de conexão com Ollama: {ex.Message}", statusCode: 500);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Erro interno: {ex.Message}", statusCode: 500);
    }
})
.WithName("GenerateHashtags")
.WithTags("Hashtags")
.WithOpenApi();

app.Run();

static string TruncateForLog(string s, int max = 300) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

static IEnumerable<string> GenerateFallbackHashtags(string text, int needed, IEnumerable<string> existing)
{
    var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
    var clean = text
        .Replace(".", "")
        .Replace(",", "")
        .Replace(":", "")
        .Replace(";", "")
        .Trim();

    var words = clean.Split(new[] { ' ', '\t', '\n', '\r', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(w => w.ToLowerInvariant())
                     .Where(w => w.Length > 1)
                     .Distinct()
                     .ToArray();

    var results = new List<string>();
    int suffix = 1;
    int wi = 0;

    while (results.Count < needed)
    {
        string candidate;
        if (words.Length == 0)
        {
            candidate = $"#{Sanitize($"hashtag{suffix}")}";
        }
        else if (words.Length == 1)
        {
            candidate = $"#{Sanitize(words[0])}{(suffix == 1 ? "" : suffix.ToString())}";
        }
        else
        {
            // combine two words rotating
            var a = words[wi % words.Length];
            var b = words[(wi + 1) % words.Length];
            candidate = $"#{Sanitize(a + b)}{(suffix == 1 ? "" : suffix.ToString())}";
        }

        if (!set.Contains(candidate) && !results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(candidate);
            set.Add(candidate);
        }

        wi++;
        if (wi % 5 == 0) suffix++;
    }

    return results;
}

static string Sanitize(string s)
{
    var allowed = s.Where(c => char.IsLetterOrDigit(c)).ToArray();
    var outS = new string(allowed);
    if (string.IsNullOrWhiteSpace(outS)) return "hashtag";
    return outS.ToLowerInvariant();
}

// request/response DTOs
public record HashtagRequest(string Text, int? Count, string Model);
