using System.Diagnostics;
using System.Globalization;
using Practica8.RagPostgres;

namespace Practica9.EvaluacionRag;

// El modo explorador: la "vista" de la P9. Escribís una pregunta y ves QUÉ
// recuperó el sistema, con qué score, y si la respuesta está grounded.
// Es donde se entienden los casos raros.
//
// Usa el Asistente de la P8 (NO el Generador de la P9): el chat tiene que
// medir el sistema real, con su prompt real (por eso el Asistente expone
// UltimoPrompt y UltimaGeneracionMs).
//
// OJO con las medidas: Indice.UltimaBusquedaMs mide SÓLO el SELECT de
// ranking (el ORDER BY con <=> y el LIMIT). NO cuenta el count(*) previo ni
// la apertura de la conexión: el retrieval COMPLETO lo mide
// Asistente.UltimaBusquedaMs, que por eso también se muestra.
public static class Explorador
{
    public static async Task RunAsync(Entorno env)
    {
        Console.WriteLine("\n=== Explorador (Ctrl+C para salir) ===");
        Console.WriteLine("  comandos: /salir · /tenant <negocio> · /k <n|auto> · /prompt · /ayuda");
        Console.WriteLine("  medidas: embed = POST /api/embed · búsqueda = SÓLO el SELECT de ranking");
        Console.WriteLine("           (el count(*) y la apertura de conexión NO se cuentan)");
        Console.WriteLine("           retrieval = conexión + count + rank · generación = POST /api/chat");
        Console.WriteLine("  veredicto: grounded = todo precio de la respuesta está en el contexto");
        Console.WriteLine("             aislamiento = los hits recuperados son del tenant pedido");
        Console.WriteLine("  el índice es el que dejó la última corrida de la batería: acá NO se re-sincroniza.");

        var http = env.HttpFactory("http://localhost:11434");
        using var httpScope = http;
        var embeddings = new Embeddings(http, env.ModelEmbed);
        var indice = new Indice(env.ConnStrPg);
        var asistente = new Asistente(http, indice, embeddings, env.ModelChat);
        var bc = new BcLectura(env.ConnStrBc);
        var mapa = await bc.NegociosAsync(Casos.TenantPepe, Casos.TenantDemo);
        if (mapa.Count == 0)
        {
            Console.WriteLine("[error] No encontré los tenants de la batería en businesscore.");
            return;
        }
        Console.WriteLine("  tenants: " + string.Join(" · ", mapa.Keys));

        var tenant = mapa.ContainsKey(Casos.TenantPepe) ? Casos.TenantPepe : mapa.Keys.First();

        while (true)
        {
            Console.Write($"\n[{tenant} · k={(asistente.ForzarContextoK is int fk ? fk.ToString() : "auto")}] > ");
            var linea = Console.ReadLine();
            // EOF (stdin cerrado, modo pipe): se sale. Sin este corte, el
            // ReadLine devuelve null para siempre y el bucle muerde la CPU.
            if (linea is null) break;
            var input = linea.Trim();
            if (input.Length == 0) continue;
            if (input.Equals("/salir", StringComparison.OrdinalIgnoreCase)) break;
            if (input.Equals("/ayuda", StringComparison.OrdinalIgnoreCase) || input == "?")
            {
                Console.WriteLine("  comandos: /salir · /tenant <negocio> · /k <n|auto> · /prompt · /ayuda");
                continue;
            }
            if (input.StartsWith("/tenant", StringComparison.OrdinalIgnoreCase))
            {
                var n = input.Length > 7 ? input[7..].Trim() : "";
                var elegido = mapa.Keys.FirstOrDefault(x => x.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (n.Length == 0 || elegido is null)
                    Console.WriteLine($"  no encontré un tenant con \"{n}\". Disponibles: {string.Join(" · ", mapa.Keys)}");
                else
                {
                    tenant = elegido;
                    Console.WriteLine($"  tenant: {tenant}");
                }
                continue;
            }
            if (input.StartsWith("/k", StringComparison.OrdinalIgnoreCase))
            {
                var arg = input.Length > 2 ? input[2..].Trim() : "";
                if (arg.Length == 0 || arg.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    // "auto": vuelve la regla adaptativa de la P7/P8 (catálogo
                    // chico completo, si no top-3).
                    indice.ForzarK = null;
                    asistente.ForzarContextoK = null;
                    Console.WriteLine("  k: automático (catálogo chico completo; si no, top-3)");
                }
                else if (int.TryParse(arg, out var nk) && nk > 0)
                {
                    // Las dos perillas: ForzarK recorta el LIMIT del SQL y
                    // ForzarContextoK recorta lo que se le manda al modelo.
                    indice.ForzarK = nk;
                    asistente.ForzarContextoK = nk;
                    Console.WriteLine($"  k forzado a {nk} (SQL y contexto)");
                }
                else
                {
                    Console.WriteLine("  /k espera un entero > 0 o \"auto\"");
                }
                continue;
            }
            if (input.Equals("/prompt", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(asistente.UltimoPrompt ?? "(todavía no se mandó ningún prompt)");
                continue;
            }

            try
            {
                await ResponderAsync(input, tenant, mapa[tenant], env, embeddings, indice, asistente);
            }
            catch (Exception ex)
            {
                // Una pregunta que falla NO mata el chat: se informa y se
                // sigue (si Ollama se cayó, el operador decide qué hacer).
                Console.WriteLine($"   [error] {ex.Message}");
            }
        }
    }

    private static async Task ResponderAsync(
        string input, string tenant, Guid tenantId, Entorno env,
        Embeddings embeddings, Indice indice, Asistente asistente)
    {
        var sw = Stopwatch.StartNew();
        var (respuesta, _citas) = await asistente.PreguntarAsync(tenantId, tenant, input);

        // Las medidas del PEDIDO que produjo la respuesta: se leen ANTES del
        // segundo retrieval (que pisa UltimaBusquedaMs con su propio número).
        var embedMs = embeddings.UltimoEmbedMs;
        var rankMs = indice.UltimaBusquedaMs;
        var retrievalMs = asistente.UltimaBusquedaMs;
        var genMs = asistente.UltimaGeneracionMs;

        // El segundo retrieval: el Asistente devuelve las citas como TEXTO
        // ("texto (score)"), sin el service_id. Para el veredicto de aislamiento
        // hay que preguntarle a la DB de qué negocio es cada chunk: hace falta
        // el id. Misma pregunta + mismo embedding → las mismas filas.
        var q = await embeddings.EmbedConsultaAsync(input);
        var hits = await indice.BuscarAsync(tenantId, q);
        sw.Stop();

        // El contexto REAL que se le mandó al modelo (con la perilla de k).
        var contexto = hits.Count == 0
            ? new List<string>()
            : hits.Take(asistente.ForzarContextoK ?? hits.Count).Select(h => h.Texto).ToList();

        var corrida = new Corrida(respuesta, hits, null, null, embedMs, rankMs, genMs, asistente.UltimoPrompt)
        {
            Contexto = contexto,
            RetrievalMs = retrievalMs,
            HitsAjenos = await Runner.HitsAjenosAsync(env.ConnStrPg, tenantId, hits)
        };

        // Los dos veredictos, con las MISMAS reglas de Verificadores.cs (no se
        // reimplementan: la regla del groundedness es de VALOR, no de texto
        // idéntico, y duplicarla acá la haría divergir de la batería):
        //   - un caso sintético del grupo "Aislamiento" → los hits son del tenant;
        //   - un caso sintético del grupo "Groundedness" → todo precio de la
        //     respuesta está en el contexto. (Al groundedness se le vacían los
        //     hits ajenos: son dos veredictos separados, no uno encadenado.)
        var verAislamiento = Verificadores.Verificar(Sintetico("Aislamiento"), corrida, null, null);
        var verGrounded = Verificadores.Verificar(Sintetico("Groundedness"), corrida with { HitsAjenos = [] }, null, null);

        Console.WriteLine($"   embed: {embedMs}ms · búsqueda (rank): {rankMs}ms · retrieval: {retrievalMs}ms · generación: {genMs}ms");
        Console.WriteLine($"   top-{hits.Count} recuperado (score = 1 - distancia coseno):");
        for (var i = 0; i < Math.Min(hits.Count, 5); i++)
            Console.WriteLine($"     {i + 1}. {hits[i].Texto} ({hits[i].Score.ToString("F2", CultureInfo.InvariantCulture)})");
        if (hits.Count > 5) Console.WriteLine($"     … y {hits.Count - 5} más");
        var kNota = asistente.ForzarContextoK is int fck ? $" (k forzado a {fck})" : "";
        Console.WriteLine($"   contexto al modelo: {contexto.Count} chunk(s){kNota}");
        if (hits.Count == 0) Console.WriteLine("   (fail-closed: no hay chunks para este tenant)");
        Console.WriteLine($"   respuesta: {respuesta}");

        var delTenant = hits.Count - corrida.HitsAjenos.Count;
        Console.WriteLine($"   veredicto: grounded {(verGrounded.Paso ? "✔" : "✘")} · " +
                          $"aislamiento {(verAislamiento.Paso ? "✔" : "✘")} ({delTenant}/{hits.Count} hits del tenant) · " +
                          $"{sw.Elapsed.TotalSeconds:F1}s");
        if (!verGrounded.Paso) Console.WriteLine($"     grounded: {verGrounded.Detalle}");
        if (!verAislamiento.Paso) Console.WriteLine($"     aislamiento: {verAislamiento.Detalle}");
    }

    // Un caso sintético: el explorador no es un caso de la batería, pero las
    // REGLAS que lo juzgan son las de Verificadores.cs (por grupo).
    private static Caso Sintetico(string grupo) => new()
    {
        Id = "X", Grupo = grupo, Tenant = "-", Pregunta = "-"
    };
}
