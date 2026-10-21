using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Practica9.EvaluacionRag;

// Generador con prompt parametrizado: es el bloque de generación de la P8 con
// el prompt como parámetro. Existe para poder COMPARAR variantes de prompt
// (escenario E2) sin tocar la copia instrumentada, que debe quedar verbatim.
//
// El prompt por defecto es el de la P8, y su fidelidad se verifica en runtime
// contra el prompt real que capturó la copia (p8/Asistente.cs → UltimoPrompt):
// si el Generador dejara de usar el prompt de la P8, todas las mediciones
// dependerían de un sistema que nadie corrió.
public sealed class Generador
{
    public string? PromptAdicional { get; set; }
    public long UltimaGeneracionMs { get; private set; }

    // El prompt de la P8, textual. Es el MISMO texto que el de p8/Asistente.cs
    // (verificado en runtime por Runner.VerificarFidelidadAsync).
    public static string PromptP8(string negocio) =>
        $"Sos el asistente de \"{negocio}\". Respondé en español rioplatense. " +
        "Usá ÚNICAMENTE el contexto. Reglas: (1) Si te preguntan por servicios/listado/precios, " +
        "enumerá TODOS los servicios del contexto, uno por línea, con su precio y duración — no resumas ni agrupes. " +
        "(2) Si te preguntan por uno solo, contestá solo ese. " +
        "(3) Si lo pedido no está en el contexto decí exactamente: \"No tengo esa información.\" No inventes.";

    public async Task<(string Respuesta, string? Prompt)> GenerarAsync(
        HttpClient http, string modelo, string negocio, List<string> contexto, string pregunta)
    {
        var prompt = PromptP8(negocio) + (PromptAdicional is null ? "" : " " + PromptAdicional);
        var body = new JsonObject
        {
            ["model"] = modelo,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = prompt },
                new JsonObject { ["role"] = "user",
                    ["content"] = $"Contexto:\n{string.Join("\n", contexto.Select((t, i) => $"[{i + 1}] {t}"))}\n\nPregunta: {pregunta}" }
            },
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0 }
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync("/api/chat", body);
        response.EnsureSuccessStatusCode();
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        var texto = doc!.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "(vacío)";
        UltimaGeneracionMs = sw.ElapsedMilliseconds;
        return (texto, prompt);
    }
}
