using System.Net.Http.Json;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opt =>
{
    opt.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Hashtag Generator API",
        Version = "v1",
        Description = "API Minimal com integração ao Ollama para geração de hashtags."
    });
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
.WithOpenApi(operation =>
{
    operation.Summary = "Retorna todas as hashtags já geradas";
    operation.Description = "Endpoint GET que retorna o histórico das hashtags geradas pelo POST.";
    return operation;
});

app.MapPost("/hashtags", async (HashtagRequest req) =>
{
    if (req.Count < 1 || req.Count > 30)
        return Results.BadRequest(new { error = "O campo 'count' deve ser entre 1 e 30." });

    if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.Model))
        return Results.BadRequest(new { error = "Text e model são obrigatórios." });

    string ollamaPrompt = $@"
Gere exatamente {req.Count} hashtags em português, sem espaços, sem duplicatas, formato JSON:
{{""model"": ""{req.Model}"", ""count"": {req.Count}, ""hashtags"": [""#hashtag1"", ""#hashtag2"", ...]}}
Tema: {req.Text}
Responda apenas com JSON válido.";

    using var http = new HttpClient();
    var ollamaRequest = new { model = req.Model, prompt = ollamaPrompt, stream = false, format = "json" };
    var ollamaResponse = await http.PostAsJsonAsync("http://localhost:11434/api/generate", ollamaRequest);

    if (!ollamaResponse.IsSuccessStatusCode)
        return Results.Problem("Falha ao consultar o Ollama.", statusCode: 500);

    OllamaResponse? rawResponse;
    try
    {
        rawResponse = await ollamaResponse.Content.ReadFromJsonAsync<OllamaResponse>();
        if (rawResponse is null || string.IsNullOrEmpty(rawResponse.response))
            return Results.Problem("Ollama não retornou resposta válida.", statusCode: 500);

        var jsonDoc = System.Text.Json.JsonDocument.Parse(rawResponse.response);
        var hashtagsElem = jsonDoc.RootElement.GetProperty("hashtags");
        var hashtags = hashtagsElem.EnumerateArray()
            .Select(x => x.GetString()!.Replace(" ", ""))
            .Distinct()
            .Take(req.Count)
            .ToList();

        var responseObj = new { model = req.Model, count = hashtags.Count, hashtags };
        hashtagHistory.Add(responseObj);
        return Results.Ok(responseObj);
    }
    catch
    {
        return Results.Problem("A resposta do Ollama não pôde ser convertida para JSON. Tente ajustar o prompt ou use outro modelo.", statusCode: 500);
    }
})
.WithName("GenerateHashtags")
.WithTags("Hashtags")
.WithOpenApi();

app.Run();

public record HashtagRequest(string Text, int Count, string Model);
public record OllamaResponse(string response);