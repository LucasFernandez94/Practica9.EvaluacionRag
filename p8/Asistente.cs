using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
// EVAL: COPIA de Practica8.RagPostgres/Asistente.cs @ 500ecad.
// EVAL: Editá SOLO con líneas marcadas // EVAL: el check de deriva compara contra el original.

namespace Practica8.RagPostgres;

// El modelo redacta, C# dispone: acá no se decide NADA de aislamiento.
// El contexto que llega al prompt ya viene filtrado por tenant desde el SQL.
public sealed class Asistente(HttpClient ollamaHttp, Indice indice, Embeddings embeddings, string chatModel)
{
    public string ChatModel { get; set; } = chatModel;

    public async Task<(string Respuesta, List<string> Citas)> PreguntarAsync(
        Guid tenantId, string negocio, string pregunta)
    {
        // 1. Embed de la PREGUNTA: una sola llamada, el contenido ya está indexado.
        var q = await embeddings.EmbedConsultaAsync(pregunta);

        // 2. Retrieval en Postgres (pgvector), con el filtro de tenant en el mismo SQL.
        var swBusqueda = System.Diagnostics.Stopwatch.StartNew(); // EVAL: medición (P9) del retrieval entero
        var hits = await indice.BuscarAsync(tenantId, q);
        UltimaBusquedaMs = swBusqueda.ElapsedMilliseconds; // EVAL: medición (P9)
        if (ForzarContextoK is int fck) hits = hits.Take(fck).ToList(); // EVAL: forzado del contexto (P9) — trunca lo que se le manda al modelo

        // 3. Fail-closed: sin contexto no hay respuesta inventada.
        if (hits.Count == 0)
            return ("No tengo esa información.", []);

        // 4. Generación grounded: el LLM solo transcribe/combina el contexto.
        var contexto = string.Join("\n", hits.Select((h, i) => $"[{i + 1}] {h.Texto}"));
        var body = new JsonObject
        {
            ["model"] = ChatModel,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system",
                    ["content"] = $"Sos el asistente de \"{negocio}\". Respondé en español rioplatense. " +
                                  "Usá ÚNICAMENTE el contexto. Reglas: (1) Si te preguntan por servicios/listado/precios, " +
                                  "enumerá TODOS los servicios del contexto, uno por línea, con su precio y duración — no resumas ni agrupes. " +
                                  "(2) Si te preguntan por uno solo, contestá solo ese. " +
                                  "(3) Si lo pedido no está en el contexto decí exactamente: \"No tengo esa información.\" No inventes." },
                new JsonObject { ["role"] = "user", ["content"] = $"Contexto:\n{contexto}\n\nPregunta: {pregunta}" }
            },
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = 0 }
        };
        try
        {
            // El `using` va ACÁ: si EnsureSuccessStatusCode() o la lectura del body
            // fallan, la respuesta se libera igual y la conexión vuelve al pool.
            var swGeneracion = System.Diagnostics.Stopwatch.StartNew(); // EVAL: medición (P9) del POST /api/chat
            using var response = await ollamaHttp.PostAsJsonAsync("/api/chat", body);
            response.EnsureSuccessStatusCode();
            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            var texto = doc!.RootElement.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "(vacío)";
            UltimaGeneracionMs = swGeneracion.ElapsedMilliseconds; // EVAL: medición (P9)
            UltimoPrompt = (body["messages"] as JsonArray)?[0]?["content"]?.GetValue<string>(); // EVAL: captura del prompt real (P9)
            // Score con cultura invariante: el valor va en las citas y se compara
            // contra los números del README ($0.77), no contra "0,77" de es-AR.
            return (texto, hits.Select(h =>
                $"{h.Texto} ({h.Score.ToString("F2", CultureInfo.InvariantCulture)})").ToList());
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"No pude hablar con Ollama. ¿Está corriendo? ¿Tenés el modelo? ollama pull {ChatModel}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException(
                $"Ollama no respondió a tiempo. ¿Está saturado? ¿Tenés el modelo? ollama pull {ChatModel}", ex);
        }
    }
    public long UltimaGeneracionMs { get; private set; } // EVAL: medición (P9)
    public int? ForzarContextoK { get; set; } // EVAL: forzado del tamaño del contexto (P9)
    public string? UltimoPrompt { get; private set; } // EVAL: captura del prompt real, para verificar que el Generador de la P9 sea fiel
    public long UltimaBusquedaMs { get; private set; } // EVAL: medición (P9) — el retrieval entero VISTO desde el asistente (conexión + count + rank), a diferencia de Indice.UltimaBusquedaMs (sólo el SELECT de ranking)
}
