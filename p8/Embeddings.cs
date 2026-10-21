using System.Net.Http.Json;
using System.Text.Json;
// EVAL: COPIA de Practica8.RagPostgres/Embeddings.cs @ 500ecad.
// EVAL: Editá SOLO con líneas marcadas // EVAL: el check de deriva compara contra el original.

namespace Practica8.RagPostgres;

// Cliente mínimo de Ollama para embeddings (nomic-embed-text, 768 dims).
// Un solo lugar donde se habla con /api/embed: lo usan el sync (documentos) y el chat (consulta).
// nomic-embed-text distingue los dos roles con prefijos: sin ellos, el modelo compara
// consultas contra documentos con el mismo prefijo y el ranking pierde precisión.
public sealed class Embeddings(HttpClient ollamaHttp, string model = "nomic-embed-text")
{
    private const string PrefijoDocumento = "search_document: ";
    private const string PrefijoConsulta = "search_query: ";

    // Documentos que se indexan: prefijo de documento.
    public Task<List<float[]>> EmbedDocumentosAsync(List<string> textos) =>
        EmbedPrefijadosAsync(textos.Select(t => PrefijoDocumento + t).ToList());

    // Consulta del usuario: prefijo de consulta (NUNCA el de documento).
    public async Task<float[]> EmbedConsultaAsync(string texto)
    {
        var r = await EmbedPrefijadosAsync([PrefijoConsulta + texto]);
        // Mismo guard que el path de sync: si Ollama no devuelve exactamente un
        // embedding, el r[0] reventaría con IndexOutOfRangeException y el error
        // que ve el usuario sería opaco.
        if (r.Count != 1)
            throw new InvalidOperationException(
                $"Ollama devolvió {r.Count} embeddings para 1 consulta.");
        return r[0];
    }

    // El prefijo lo aplica quien llama: el texto que sale de acá ya va prefixado,
    // así la consulta nunca termina con dos prefijos encadenados.
    private async Task<List<float[]>> EmbedPrefijadosAsync(List<string> prefijados)
    {
        if (prefijados.Count == 0) return [];
        var sw = System.Diagnostics.Stopwatch.StartNew(); // EVAL: medición (P9) del POST /api/embed
        try
        {
            // El `using` va ACÁ: si EnsureSuccessStatusCode() o la lectura del body
            // fallan, la respuesta se libera igual y la conexión vuelve al pool.
            using var response = await ollamaHttp.PostAsJsonAsync("/api/embed",
                new { model = model, input = prefijados });
            response.EnsureSuccessStatusCode();
            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            UltimoEmbedMs = sw.ElapsedMilliseconds; // EVAL: medición (P9)
            return doc!.RootElement.GetProperty("embeddings").EnumerateArray()
                .Select(e => e.EnumerateArray().Select(n => n.GetSingle()).ToArray()).ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"No pude hablar con Ollama para los embeddings. ¿Está corriendo? ¿Tenés el modelo? ollama pull {model}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException(
                $"Ollama no respondió a tiempo para los embeddings. ¿Está saturado? ¿Tenés el modelo? ollama pull {model}", ex);
        }
    }
    public long UltimoEmbedMs { get; private set; } // EVAL: medición (P9) — el original no tiene esto
}
