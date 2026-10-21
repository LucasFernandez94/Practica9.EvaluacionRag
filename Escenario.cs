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

    // El retrieval COMPLETO (apertura de conexión + count(*) + ranking), medido
    // por el runner con un cronómetro propio alrededor de la búsqueda.
    //
    // Es la columna que faltaba: BusquedaMs mide SÓLO el SELECT de ranking
    // (que es sub-milisegundo y por eso lee ~0), así que sin esta columna el
    // costo de SQL no estaba en ninguna parte y "SQL ≈ 0 ms" era una afirmación
    // sobre dónde estaba el cronómetro, no sobre el sistema.
    public long RetrievalMs { get; init; }

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

// Un servicio del catálogo VIVO de BusinessCore: su id y el texto canónico
// EXACTO que se indexa (BcLectura.TextoCanonico). Es la unidad del ground
// truth dinámico: el KValido se calcula contra esta lista, no contra un
// número hardcodeado en el caso.
public sealed record ServicioCanonico(Guid Id, string Texto);

// El catálogo canónico de un tenant en un momento dado: los servicios vivos
// de BC y el texto que se indexa. Lo usa el sync Y el ground truth.
public sealed record CatalogoTenant(
    string Tenant, Guid BusinessId, IReadOnlyList<ServicioCanonico> Servicios);

// El tamaño del catálogo vivo por tenant. Va al boletín: el ground truth se
// midió contra ESTE catálogo, y el catálogo es un moving target (el de
// BusinessCore Demo pasó de 6 a 28 servicios durante la práctica).
//
// Servicios = el tamaño con el que se midió (al empezar el escenario).
// ServiciosAlFinal = el tamaño con el que se terminó: el de BC lo modifica un
// proceso externo (la suite de BusinessCore), así que los dos números se
// registran para que el boletín diga con qué corpus se midió y con cuál se
// terminó.
public sealed record CatalogReporte(string Tenant, int Servicios, int ServiciosAlFinal);

public sealed record Resumen(
    string Escenario, string Modelo, int Total, int Pass, double Tasa,
    double SegPromedio, double SegP90,
    Dictionary<string, ResumenGrupo> PorGrupo)
{
    // El sync del escenario: se agrega después de Calcular (el resumen no sabe
    // del índice, el runner sí).
    public IReadOnlyList<SyncReporte> Sync { get; init; } = [];

    // El catálogo vivo contra el que se calculó el ground truth dinámico.
    public IReadOnlyList<CatalogReporte> Catalogo { get; init; } = [];

    // Los casos que no se pudieron verificar (no son pass ni fail).
    public int Saltados { get; init; }

    // El contrato literal del prompt, a nivel de escenario: de los casos que
    // declaran ExigeNoInfo, cuántos respondieron con la frase exacta
    // "No tengo esa información.". Es una MÉTRICA, no un gate.
    [JsonPropertyName("casos_no_info")] public int CasosNoInfo { get; init; }
    [JsonPropertyName("frases_exactas")] public int FrasesExactas { get; init; }

    // Los p50/p95 por etapa (spec §5.2): embed, retrieval (el SQL completo),
    // rank (el SELECT de ranking solo) y generación. Es la respuesta a "¿cuánto
    // es embedding, cuánto es SQL y cuánto es el modelo?".
    public IReadOnlyList<EtapaResumen> Etapas { get; init; } = [];

    public EtapaResumen? Etapa(string nombre) =>
        Etapas.FirstOrDefault(e => e.Etapa == nombre);

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
            FrasesExactas = rs.Count(r => r.FraseExacta == true),
            Etapas = EtapasDe(rs)
        };
    }

    // Los p50/p95 por etapa. Los percentiles se calculan sobre TODOS los casos
    // del escenario (los que no hicieron esa etapa valen 0): el rank es
    // sub-milisegundo, así que un 0 ahí es una MEDICIÓN, no un "no corrió".
    // El índice es el mismo que usa el p90 del total (percentil por rango,
    // sobre la lista ordenada). El conteo que se reporta es el de CASOS
    // considerados (ver EtapaResumen).
    private static IReadOnlyList<EtapaResumen> EtapasDe(List<Resultado> rs)
    {
        var etapas = new (string Nombre, Func<Resultado, long> Valor)[]
        {
            ("embed", r => r.EmbedMs),
            ("retrieval", r => r.RetrievalMs),
            ("rank", r => r.BusquedaMs),
            ("generacion", r => r.GeneracionMs),
        };
        return etapas.Select(e =>
        {
            var vals = rs.Select(e.Valor).OrderBy(v => v).ToList();
            return new EtapaResumen(e.Nombre, Percentil(vals, 0.5), Percentil(vals, 0.95), vals.Count);
        }).ToList();
    }

    private static long Percentil(List<long> ordenados, double q)
    {
        if (ordenados.Count == 0) return 0;
        var i = (int)(ordenados.Count * q);
        if (i >= ordenados.Count) i = ordenados.Count - 1;
        if (i < 0) i = 0;
        return ordenados[i];
    }
}

// El resumen de UNA etapa: el p50 y el p95 (en ms) y cuántos casos se
// consideraron.
//
// "Casos" (no "mediciones"): el percentil se calcula sobre TODOS los casos
// del escenario y los que NO hicieron esa etapa aportan 0 (los 4 casos D, B3
// (respuesta sintética), B4 y B5, y los que fallan antes de generar). El 0 de
// un caso que no midió la etapa es indistinguible del 0 real de una etapa
// sub-milisegundo (el rank), así que el conteo honesto es el de CASOS
// considerados, y la convención de los ceros está documentada en el README.
public sealed record EtapaResumen(string Etapa, long P50, long P95, int Casos);

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

    // Los service_id de los chunks que COMPUSIERON ese contexto real, en el
    // mismo orden. El ground truth del retrieval se decide por ID (el texto
    // canónico identifica al servicio), no parseando el chunk: por eso el
    // contexto lleva su lista de ids y no sólo sus textos.
    public IReadOnlyList<Guid> ContextoIds { get; init; } = [];

    // La respuesta NO la produjo el modelo: la armó el runner (el fail-closed
    // del tenant vacío, B3). Sirve para que el verificador no atribuya al
    // modelo el cumplimiento del contrato literal del prompt.
    public bool RespuestaSintetica { get; init; }

    // Los service_id de los hits que NO son del business_id pedido. Lo calcula
    // el runner con una consulta a la DB (nunca parseando el texto del chunk):
    // es la garantía central del grupo A.
    public IReadOnlyList<Guid> HitsAjenos { get; init; } = [];

    // El retrieval COMPLETO (conexión + count(*) + ranking), medido por el
    // runner con su propio cronómetro. Distinto de BusquedaMs, que mide SÓLO
    // el SELECT de ranking (y por eso lee ~0: es sub-milisegundo).
    public long RetrievalMs { get; init; }
}
