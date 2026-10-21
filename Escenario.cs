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
    string Obtenido, string Detalle);

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

    public static Resumen Calcular(string escenario, string modelo, List<Resultado> rs)
    {
        var segs = rs.Select(r => r.Segundos).OrderBy(s => s).ToList();
        var pass = rs.Count(r => r.Paso);
        return new Resumen(escenario, modelo, rs.Count, pass,
            rs.Count == 0 ? 0 : (double)pass / rs.Count,
            segs.Count == 0 ? 0 : segs.Average(),
            segs.Count == 0 ? 0 : segs[Math.Min(segs.Count - 1, (int)(segs.Count * 0.9))],
            rs.GroupBy(r => r.Grupo)
               .ToDictionary(g => g.Key, g => new ResumenGrupo(g.Count(r => r.Paso), g.Count())));
    }
}

// El sub-resumen por grupo. Es un record y no una tupla (int Pass, int Total)
// porque las tuplas de System.Text.Json se serializan como {} (sus members son
// campos, no propiedades) y el boletín perdía el conteo por grupo.
public sealed record ResumenGrupo(int Pass, int Total);

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

    // Los service_id de los hits que NO son del business_id pedido. Lo calcula
    // el runner con una consulta a la DB (nunca parseando el texto del chunk):
    // es la garantía central del grupo A.
    public IReadOnlyList<Guid> HitsAjenos { get; init; } = [];
}
