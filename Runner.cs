using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Practica8.RagPostgres;

namespace Practica9.EvaluacionRag;

// Corre un escenario: un set de casos + una configuración (modelo, prompt, k).
// Imprime la tabla y escribe resultados-{escenario}.json / .csv (el boletín).
public static class Runner
{
    public static async Task<Resumen> CorrerAsync(
        IReadOnlyList<Caso> casos, Escenario esc, Entorno env, string salidaBase)
    {
        Console.WriteLine($"\n===== {esc.Nombre} · {esc.Modelo} =====");

        var http = env.HttpFactory("http://localhost:11434");
        using var httpScope = http;
        var embeddings = new Embeddings(http, env.ModelEmbed);
        var indice = new Indice(env.ConnStrPg);
        var generador = new Generador { PromptAdicional = esc.PromptAdicional };

        // El tenant: el mapa de negocios de BC, igual que la P8.
        var bc = new BcLectura(env.ConnStrBc);
        var mapa = await bc.NegociosAsync(Casos.TenantPepe, Casos.TenantDemo);
        if (!mapa.ContainsKey(Casos.TenantPepe) || !mapa.ContainsKey(Casos.TenantDemo))
            throw new InvalidOperationException(
                "No encontré los dos tenants de la batería en businesscore " +
                $"('{Casos.TenantPepe}' y '{Casos.TenantDemo}').");

        // 1) Sync del índice. Si el índice está desactualizado, los casos de
        //    retrieval fallan por una razón que NO es del sistema: el número
        //    del sync queda escrito en la salida y en el boletín.
        var sync = await SincronizarAsync(indice, bc, mapa, embeddings);
        foreach (var s in sync)
            Console.WriteLine($"  sync · {s.Tenant}: {s.Nuevos} nuevos, {s.Actualizados} actualizados, {s.Borrados} borrados");

        // 2) Fidelidad del Generador contra el prompt REAL de la P8. Una sola
        //    vez por escenario: si difieren, todas las mediciones dependerían de
        //    un prompt que no es el de la P8 y no tiene sentido seguir.
        await VerificarFidelidadAsync(http, esc, mapa, indice, embeddings);

        // La deriva: si la P8 no está, el grupo D se SKIPEA.
        var raizP8 = Deriva.RaizP8Desde(env.RaizP9);
        var derivaCtx = raizP8 is null
            ? new DerivaContext([], false)
            : new DerivaContext(Deriva.Verificar(raizP8, env.RaizP9), true);

        var resultados = new List<Resultado>();
        foreach (var caso in casos)
        {
            var sw = Stopwatch.StartNew();
            Corrida corrida;
            try
            {
                corrida = await EjecutarAsync(caso, esc, env, mapa, embeddings, indice, generador);
            }
            catch (Exception ex)
            {
                corrida = new Corrida("", [], ex.Message, ClasificarError(ex), 0, 0, 0, null);
            }
            sw.Stop();

            var veredicto = Verificadores.Verificar(caso, corrida, derivaCtx);
            var r = new Resultado(caso.Id, caso.Grupo, esc.Nombre, esc.Modelo,
                veredicto.Paso, sw.Elapsed.TotalSeconds,
                corrida.EmbedMs, corrida.BusquedaMs, corrida.GeneracionMs,
                Recorte(corrida.Respuesta), veredicto.Detalle);
            resultados.Add(r);
            Console.WriteLine($"  {r.Id,-4} {r.Grupo,-12} {(r.Paso ? "✔" : "✘")} {r.Segundos,5:F1}s  {r.Detalle}");
        }

        var resumen = Resumen.Calcular(esc.Nombre, esc.Modelo, resultados) with { Sync = sync };
        Console.WriteLine($"\n  → {resumen.Pass}/{resumen.Total} ({resumen.Tasa:P0}) · prom {resumen.SegPromedio:F1}s · p90 {resumen.SegP90:F1}s");
        foreach (var g in resumen.PorGrupo.OrderBy(g => g.Key))
            Console.WriteLine($"    {g.Key,-12}: {g.Value.Pass}/{g.Value.Total}");

        Escribir(resultados, resumen, salidaBase, esc);
        return resumen;
    }

    // El sync: el índice se repuebla desde BC con el SyncAsync de la P8 (nada
    // nuevo: no se crea ninguna tabla ni se siembran datos, sólo se mantiene
    // bc_service_chunks al día con los servicios reales de los dos tenants).
    private static async Task<List<SyncReporte>> SincronizarAsync(
        Indice indice, BcLectura bc, Dictionary<string, Guid> mapa, Embeddings embeddings)
    {
        var reportes = new List<SyncReporte>();
        foreach (var (nombre, id) in mapa)
        {
            var servicios = await bc.ServiciosDeAsync(id);
            var actuales = servicios
                .Select(s => { var t = BcLectura.TextoCanonico(s); return (s.Id, Texto: t, Hash: BcLectura.Hash(t)); })
                .ToList();
            var rep = await indice.SyncAsync(id, actuales, embeddings);
            reportes.Add(new SyncReporte(nombre, rep.Nuevos, rep.Actualizados, rep.Borrados));
        }
        return reportes;
    }

    // La fidelidad del Generador: una llamada REAL al Asistente de p8/ (que
    // captura UltimoPrompt) y la comparación con Generador.PromptP8(negocio).
    // Si difieren → error y se para: todas las mediciones quedarían hechas
    // contra un prompt que no es el de la P8.
    private static async Task VerificarFidelidadAsync(
        HttpClient http, Escenario esc, Dictionary<string, Guid> mapa, Indice indice, Embeddings embeddings)
    {
        var tenant = mapa.ContainsKey(Casos.TenantPepe) ? Casos.TenantPepe : mapa.Keys.First();
        var asistente = new Asistente(http, indice, embeddings, esc.Modelo);
        string? real;
        try
        {
            await asistente.PreguntarAsync(mapa[tenant], tenant, "¿cuánto sale Panchos?");
            real = asistente.UltimoPrompt;
        }
        catch (Exception ex)
        {
            throw new FidelidadDelPromptException(
                $"No pude verificar la fidelidad del prompt contra la P8: {ex.Message}", ex);
        }
        if (real is null)
            throw new FidelidadDelPromptException(
                "La P8 no capturó el prompt (UltimoPrompt = null): no hay con qué comparar.");
        var esperado = Generador.PromptP8(tenant);
        if (!string.Equals(real, esperado, StringComparison.Ordinal))
            throw new FidelidadDelPromptException(
                "El prompt del Generador NO es el de la P8.\n" +
                $"    P8        : {real}\n" +
                $"    Generador : {esperado}");
        Console.WriteLine($"  fidelidad · el prompt del Generador es byte-idéntico al de la P8 ({tenant})");
    }

    private static async Task<Corrida> EjecutarAsync(
        Caso caso, Escenario esc, Entorno env, Dictionary<string, Guid> mapa,
        Embeddings embeddings, Indice indice, Generador generador)
    {
        // El grupo D no pregunta nada: verifica los archivos.
        if (caso.Grupo == "Deriva")
            return new Corrida("", [], null, null, 0, 0, 0, null);

        var tenantId = mapa[caso.Tenant];

        // B1: Ollama caído → base URL a un puerto sin servicio. Mismo path de error
        // que apagarlo, sin tocar el entorno.
        // B5: un server falso que devuelve {"embeddings":[]} → el guard del cliente
        // tiene que dar un error legible. El fake es del lado P9 (un handler), no
        // se toca la copia de p8/Embeddings.cs.
        using var http = caso.ExigeError switch
        {
            "conexion" => env.HttpFactory("http://localhost:5999"),
            "embeddings" => new HttpClient(new EmbeddingsVaciosHandler())
                { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(5) },
            _ => env.HttpFactory("http://localhost:11434")
        };

        // B2: modelo inexistente. El resto usa el del escenario.
        var modelo = caso.ExigeError == "modelo" ? "no-existe:0b" : esc.Modelo;

        // El cliente de embeddings: en B5 tiene que ser el que pegue al fake.
        var emb = caso.ExigeError == "embeddings" ? new Embeddings(http, env.ModelEmbed) : embeddings;

        // B3: tenant vacío → fail-closed, NO es un error. El caso lo declara con
        // ExigeNoInfo (no con ExigeError), así que el runner lo reconoce por esa
        // combinación: es el único caso de Robustez que pide la respuesta de
        // fail-closed en vez de una excepción.
        if (caso.Grupo == "Robustez" && caso.ExigeNoInfo)
        {
            var aleatorio = Guid.NewGuid();
            var q = await emb.EmbedConsultaAsync(caso.Pregunta);
            var hits = await indice.BuscarAsync(aleatorio, q);
            var texto = hits.Count == 0 ? "No tengo esa información." : "hits inesperados";
            return new Corrida(texto, hits, null, null,
                emb.UltimoEmbedMs, indice.UltimaBusquedaMs, 0, null);
        }

        // B5: el guard del cliente (vectores.Count != 1) tiene que disparar.
        if (caso.ExigeError == "embeddings")
        {
            try
            {
                var q = await emb.EmbedConsultaAsync(caso.Pregunta);
                return new Corrida("", [], null, null, emb.UltimoEmbedMs, 0, 0, null);
            }
            catch (Exception ex)
            {
                return new Corrida("", [], ex.Message, ClasificarError(ex), emb.UltimoEmbedMs, 0, 0, null);
            }
        }

        // B4: vector de 4 dims contra vector(768) → Postgres responde 22000.
        if (caso.ExigeError == "dimensión")
        {
            try
            {
                await indice.BuscarAsync(tenantId, new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
                return new Corrida("", [], null, null, 0, 0, 0, null);
            }
            catch (Exception ex)
            {
                var cat = EsErrorDeDimension(ex) ? "dimensión" : "desconocida";
                return new Corrida("", [], ex.Message, cat, 0, 0, 0, null);
            }
        }

        // B1 y B2: el error tiene que aparecer en la generación (que es lo que
        // llama a Ollama con el modelo).
        if (caso.ExigeError is "conexion" or "modelo")
        {
            try
            {
                var (r, _) = await generador.GenerarAsync(http, modelo, caso.Tenant, ["contexto"], caso.Pregunta);
                return new Corrida(r, [], null, null, 0, 0, 0, null);
            }
            catch (Exception ex)
            {
                return new Corrida("", [], ex.Message, ClasificarError(ex), 0, 0, 0, null);
            }
        }

        // Camino normal: retrieval con el Indice de la P8 + generación.
        var q2 = await emb.EmbedConsultaAsync(caso.Pregunta);
        var q2ms = emb.UltimoEmbedMs;

        indice.ForzarK = esc.ForzarK;
        var hits2 = await indice.BuscarAsync(tenantId, q2);
        // ÍNDICE: UltimaBusquedaMs mide SÓLO el SELECT de ranking (el ORDER BY
        // con <=> y el LIMIT): NO el count(*) previo ni la apertura de la
        // conexión. El retrieval COMPLETO (conexión + count + ranking) lo mide
        // Asistente.UltimaBusquedaMs, que no aplica acá porque el runner usa
        // el Generador, no el Asistente.
        var busquedaMs = indice.UltimaBusquedaMs;

        // La pertenencia al tenant: cada service_id del hit tiene que ser del
        // business_id pedido. Se pregunta a la DB (no se parsea el texto).
        var ajenos = await HitsAjenosAsync(env.ConnStrPg, tenantId, hits2);

        if (hits2.Count == 0)
            return new Corrida("No tengo esa información.", [], null, null, q2ms, busquedaMs, 0, null)
            { HitsAjenos = ajenos };

        var contexto = hits2.Select(h => h.Texto).ToList();
        if (esc.ForzarContextoK is int fck) contexto = contexto.Take(fck).ToList();

        var (respuesta, prompt) = await generador.GenerarAsync(http, modelo, caso.Tenant, contexto, caso.Pregunta);
        return new Corrida(respuesta, hits2, null, null, q2ms, busquedaMs, generador.UltimaGeneracionMs, prompt)
        { Contexto = contexto, HitsAjenos = ajenos };
    }

    // Los hits que NO son del tenant pedido. Se consulta la tabla del índice por
    // service_id (el id real del chunk), así que la pertenencia se decide con
    // el dato, no con el texto del chunk.
    private static async Task<IReadOnlyList<Guid>> HitsAjenosAsync(
        string connStr, Guid businessId, IReadOnlyList<Indice.Hit> hits)
    {
        if (hits.Count == 0) return [];
        var ids = hits.Select(h => h.ServiceId).ToArray();
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT service_id, business_id FROM bc_service_chunks WHERE service_id = ANY(@ids)", conn);
        cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
        var delOtro = new List<Guid>();
        var vistos = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var sid = r.GetGuid(0);
            var bid = r.GetGuid(1);
            vistos.Add(sid);
            if (bid != businessId) delOtro.Add(sid);
        }
        // Un hit cuyo service_id no está en la tabla tampoco es del tenant:
        // no se puede probar su pertenencia.
        foreach (var id in ids)
            if (!vistos.Contains(id)) delOtro.Add(id);
        return delOtro;
    }

    // La categoría se saca de TODA la cadena de excepciones: la P8 envuelve
    // TODOS los fallos de Ollama con el mismo mensaje ("No pude hablar con
    // Ollama... ollama pull X"), así que el mensaje NO distingue "caído" de
    // "modelo inexistente". Hay que mirar el inner: 404 = modelo, connection
    // refused = conexión.
    //
    // El caso B2 verifica el 404 (HttpRequestException.StatusCode == 404) del
    // modelo inexistente; B1, el error de socket del puerto muerto. Los dos
    // terminan en el MISMO InvalidOperationException de la P8: sólo la cadena
    // los separa.
    public static string ClasificarError(Exception ex)
    {
        var cadena = ex.ToString();
        if (cadena.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
            cadena.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)) return "conexion";
        if (cadena.Contains("404", StringComparison.Ordinal) ||
            cadena.Contains("Not Found", StringComparison.OrdinalIgnoreCase)) return "modelo";
        if (cadena.Contains("embeddings para", StringComparison.OrdinalIgnoreCase)) return "embeddings";
        if (cadena.Contains("dimensions", StringComparison.OrdinalIgnoreCase) ||
            cadena.Contains("22000", StringComparison.Ordinal)) return "dimensión";
        return "desconocida";
    }

    // La dimensión que no cierra: Postgres responde 22000 ("expected 768
    // dimensions, not 4"). El SQLSTATE no viene en el Message, así que se
    // mira la propiedad y, además, el texto.
    private static bool EsErrorDeDimension(Exception ex) =>
        ex is PostgresException pg && pg.SqlState == "22000" ||
        ex.Message.Contains("dimensions", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("22000", StringComparison.Ordinal);

    private static void Escribir(List<Resultado> rs, Resumen resumen, string salidaBase, Escenario esc)
    {
        var json = JsonSerializer.Serialize(new
        {
            escenario = esc.Nombre,
            modelo = esc.Modelo,
            sync = resumen.Sync,
            resumen,
            resultados = rs
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText($"{salidaBase}-{esc.Nombre}.json", json);

        // El sync también va en el CSV (totales del escenario) para que el
        // boletín tenga el estado del índice sin depender del JSON.
        var nuevos = resumen.Sync.Sum(s => s.Nuevos);
        var actualizados = resumen.Sync.Sum(s => s.Actualizados);
        var borrados = resumen.Sync.Sum(s => s.Borrados);
        var csv = "id,grupo,escenario,modelo,paso,segundos,embed_ms,busqueda_ms,generacion_ms,obtenido,detalle," +
                  "sync_nuevos,sync_actualizados,sync_borrados\n" +
            string.Join("\n", rs.Select(r => string.Join(",",
                r.Id, r.Grupo, r.Escenario, r.Modelo, r.Paso ? 1 : 0,
                r.Segundos.ToString("F2", CultureInfo.InvariantCulture),
                r.EmbedMs, r.BusquedaMs, r.GeneracionMs,
                $"\"{Csv(r.Obtenido)}\"", $"\"{Csv(r.Detalle)}\"",
                nuevos, actualizados, borrados)));
        File.WriteAllText($"{salidaBase}-{esc.Nombre}.csv", csv);
    }

    private static string Csv(string s) => s.Replace("\"", "'").Replace('\n', ' ').Replace('\r', ' ');
    private static string Recorte(string s) => s.Length <= 90 ? s : s[..90] + "…";
}

// El prompt del Generador no es el de la P8: se para TODO. Es un error de la
// batería, no del sistema bajo prueba.
public sealed class FidelidadDelPromptException(string mensaje, Exception? inner = null)
    : Exception(mensaje, inner);

// Para el caso B5: un server falso que responde 200 con {"embeddings":[]},
// como si Ollama no devolviera nada. El fake es del lado P9 (un handler HTTP),
// así que la copia de p8/Embeddings.cs queda intacta: lo que se prueba es el
// GUARD del cliente real, no un cliente inventado.
public sealed class EmbeddingsVaciosHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var json = """{"embeddings":[]}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
