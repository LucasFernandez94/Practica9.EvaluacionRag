using System.Text.Json.Serialization;
using Practica8.RagPostgres;

namespace Practica9.EvaluacionRag;

// Un escenario es "el mismo set de casos, corrido de otra forma". Es lo que
// convierte la batería en una herramienta de decisión y no en un test que pasa.
public sealed record Escenario(
    string Nombre,
    string Modelo,
    string? PromptAdicional = null,
    int? ForzarK = null,
    int? ForzarContextoK = null);

public sealed record Resultado(
    string Id, string Grupo, string Escenario, string Modelo,
    bool Paso, double Segundos,
    long EmbedMs, long BusquedaMs, long GeneracionMs,
    string Obtenido, string Detalle)
{
    // El caso no se pudo verificar (P8 hermana ausente): NO es un pass. Se
    // cuenta aparte en el resumen para que "4/4" no sea un verde falso.
    public bool Skipped { get; init; }

    // El contrato LITERAL del prompt: el modelo respondió exactamente con
    // "No tengo esa información.". Sólo tiene sentido en los casos que declaran
    // ExigeNoInfo (en el resto es null). NO es un pass/fail: el pass lo decide
    // el rechazo semántico; esto informa cuánto se cumple la letra del prompt.
    [JsonPropertyName("frase_exacta")]
    public bool? FraseExacta { get; init; }
}

// El reporte de sync por tenant. Va en la salida de consola Y en el boletín:
// si el índice estaba desactualizado, los casos de retrieval fallarían por una
// razón que no es del sistema, así que el número tiene que quedar escrito.
public sealed record SyncReporte(string Tenant, int Nuevos, int Actualizados, int Borrados);

public sealed record Resumen(
    string Escenario, string Modelo, int Total, int Pass, double Tasa,
    double SegPromedio, double SegP90,
    Dictionary<string, ResumenGrupo> PorGrupo)
{
    // El sync del escenario: se agrega después de Calcular (el resumen no sabe
    // del índice, el runner sí).
    public IReadOnlyList<SyncReporte> Sync { get; init; } = [];

    // Los casos que no se pudieron verificar (no son pass ni fail).
    public int Saltados { get; init; }

    // El contrato literal del prompt, a nivel de escenario: de los casos que
    // declaran ExigeNoInfo, cuántos respondieron con la frase exacta
    // "No tengo esa información.". Es una MÉTRICA, no un gate.
    [JsonPropertyName("casos_no_info")] public int CasosNoInfo { get; init; }
    [JsonPropertyName("frases_exactas")] public int FrasesExactas { get; init; }

    public static Resumen Calcular(string escenario, string modelo, List<Resultado> rs)
    {
        var segs = rs.Select(r => r.Segundos).OrderBy(s => s).ToList();
        var pass = rs.Count(r => r.Paso);
        return new Resumen(escenario, modelo, rs.Count, pass,
            rs.Count == 0 ? 0 : (double)pass / rs.Count,
            segs.Count == 0 ? 0 : segs.Average(),
            segs.Count == 0 ? 0 : segs[Math.Min(segs.Count - 1, (int)(segs.Count * 0.9))],
            rs.GroupBy(r => r.Grupo)
               .ToDictionary(g => g.Key, g => new ResumenGrupo(
                   g.Count(r => r.Paso), g.Count(), g.Count(r => r.Skipped))
               {
                   CasosNoInfo = g.Count(r => r.FraseExacta is not null),
                   FrasesExactas = g.Count(r => r.FraseExacta == true)
               }))
        {
            Saltados = rs.Count(r => r.Skipped),
            CasosNoInfo = rs.Count(r => r.FraseExacta is not null),
            FrasesExactas = rs.Count(r => r.FraseExacta == true)
        };
    }
}

// El sub-resumen por grupo. Es un record y no una tupla (int Pass, int Total)
// porque las tuplas de System.Text.Json se serializan como {} (sus members son
// campos, no propiedades) y el boletín perdía el conteo por grupo.
public sealed record ResumenGrupo(int Pass, int Total, int Saltados = 0)
{
    // Los casos del grupo que declaran ExigeNoInfo (cuántos) y cuántos de
    // ellos respondieron con la frase LITERAL del prompt. No es un gate.
    [JsonPropertyName("casos_no_info")] public int CasosNoInfo { get; init; }
    [JsonPropertyName("frases_exactas")] public int FrasesExactas { get; init; }
}

// El entorno: todo lo que la batería necesita para correr. Las credenciales
// entran por constructor, nunca hardcodeadas.
public sealed class Entorno
{
    public required string ConnStrBc { get; init; }
    public required string ConnStrPg { get; init; }
    public required string ModelChat { get; init; }
    public required string ModelEmbed { get; init; }
    public required string RaizP8 { get; init; }
    public required string RaizP9 { get; init; }
    public required Func<string, HttpClient> HttpFactory { get; init; }
}

// Lo que pasó en UN caso: la respuesta, lo que se recuperó, los tiempos por
// etapa y el prompt que se mandó.
public sealed record Corrida(
    string Respuesta,
    List<Indice.Hit> Hits,
    string? Error,
    string? CategoriaError,
    long EmbedMs,
    long BusquedaMs,
    long GeneracionMs,
    string? Prompt)
{
    // El contexto REAL que se le mandó al modelo (puede estar truncado por la
    // perilla ForzarContextoK): el groundedness se mide contra esto, no contra
    // los hits crudos.
    public List<string> Contexto { get; init; } = [];

    // La respuesta NO la produjo el modelo: la armó el runner (el fail-closed
    // del tenant vacío, B3). Sirve para que el verificador no atribuya al
    // modelo el cumplimiento del contrato literal del prompt.
    public bool RespuestaSintetica { get; init; }

    // Los service_id de los hits que NO son del business_id pedido. Lo calcula
    // el runner con una consulta a la DB (nunca parseando el texto del chunk):
    // es la garantía central del grupo A.
    public IReadOnlyList<Guid> HitsAjenos { get; init; } = [];
}
