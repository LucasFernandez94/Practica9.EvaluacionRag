using Npgsql;
using Practica9.EvaluacionRag;

Console.WriteLine("=== Práctica 9 · Batería de evaluación del RAG vectorial ===");

var raizP9 = AppContext.BaseDirectory;
// sube hasta encontrar el .csproj (F5 corre desde bin/)
var raizResuelta = false;
for (var i = 0; i < 6; i++)
{
    if (File.Exists(Path.Combine(raizP9, "Practica9.EvaluacionRag.csproj"))) { raizResuelta = true; break; }
    var padre = Directory.GetParent(raizP9)?.FullName;
    if (padre is null) break;
    raizP9 = padre;
}

// Si la raíz no se resolvió, el check no se puede correr: eso NO es un skip de la
// P8 hermana, es un fallo. Si se deja pasar, la red se evapora en silencio.
if (!raizResuelta || !Directory.Exists(Path.Combine(raizP9, "p8")))
{
    Console.WriteLine("[error] No pude resolver la raíz del proyecto (falta el .csproj o el directorio p8/).");
    return 1;
}

var bcPass = LeerSecret("BC_PG_PASSWORD");
var pgPass = LeerSecret("PRACTICAS_PG_PASSWORD");
if (bcPass is null || pgPass is null)
{
    Console.WriteLine("Faltan credenciales. Creá un .env con BC_PG_PASSWORD y PRACTICAS_PG_PASSWORD.");
    return 1;
}

// El MODO es el primer argumento que NO empieza con "--": los que empiezan
// con "--" son flags (--rapido, --solo=<grupo>). Antes se tomaba el primer
// argumento y "dotnet run -- --rapido comparar" corría la batería en vez de
// la comparativa.
var modo = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant() ?? "bateria";
var rapido = args.Contains("--rapido", StringComparer.OrdinalIgnoreCase);
var solo = args.FirstOrDefault(a => a.StartsWith("--solo=", StringComparison.Ordinal))?.Split('=', 2)[1];
var flagsDesconocidos = args
    .Where(a => a.StartsWith("--", StringComparison.Ordinal)
             && !string.Equals(a, "--rapido", StringComparison.OrdinalIgnoreCase)
             && !a.StartsWith("--solo=", StringComparison.Ordinal))
    .ToList();

// Un modo o un flag desconocido NO dispara una corrida de 15 minutos: se
// muestra la ayuda y se sale. ("dotnet run -- --comparar" no es un modo:
// comparar no lleva guiones.)
if (modo is not ("bateria" or "comparar" or "explorar" or "casos") || flagsDesconocidos.Count > 0)
{
    Ayuda(modo, flagsDesconocidos);
    return 0;
}

// Los casos: --solo=<grupo> gana sobre --rapido.
var casos = solo is not null ? Casos.PorGrupo(solo) : (rapido ? Casos.Rapido : Casos.Todos);
if (casos.Count == 0)
{
    Console.WriteLine($"[error] El grupo \"{solo}\" no existe. " +
                      "Grupos: R, A, I, G, B, D (o el nombre completo: Retrieval, Aislamiento, " +
                      "Inyección, Groundedness, Robustez, Deriva).");
    return 1;
}

// Una corrida PARCIAL (--rapido o --solo=<grupo>) escribe en OTRA base: los
// boletines completos (resultados-*.csv/.json) son el entregable de la
// práctica, y una corrida parcial no puede pisarlos con otros totales
// (29 casos en vez de 36, o 6) sin que nadie se entere. La corrida completa
// (sin flags) sigue escribiendo en "resultados-" como siempre.
var parcial = rapido || solo is not null;
var salidaBase = parcial ? "resultados-parcial" : "resultados";

// La deriva: si la P8 hermana no está, se SKIPEA (la batería tiene que poder
// correr sola) y los casos D quedan marcados SKIPPED. Si la P8 está pero las
// copias derivaron, los números no son de fiar: se corta.
var raizP8 = Deriva.RaizP8Desde(raizP9);
if (raizP8 is null)
{
    Console.WriteLine("[skip] No encontré ../Practica8.RagPostgres: los casos de deriva se marcan SKIPPED.");
}
else
{
    Console.WriteLine($"\n--- Check de deriva contra {raizP8} ---");
    var drift = Deriva.Verificar(raizP8, raizP9);
    foreach (var d in drift)
        Console.WriteLine($"  {(d.Igual ? "✔" : "✘")} {d.Archivo}: {d.Detalle}");
    Console.WriteLine($"\n  → {drift.Count(d => d.Igual)}/{drift.Count} archivos sin deriva.");
    if (!drift.All(d => d.Igual))
    {
        Console.WriteLine("[error] Las copias de p8/ derivaron del original: las mediciones no serían de fiar.");
        return 1;
    }
}

// BC es de solo lectura por diseño: la sesión se abre en modo read-only y
// Postgres rechaza cualquier escritura (igual que en la P8).
var connBc = new NpgsqlConnectionStringBuilder
{
    Host = "localhost", Port = 5432, Database = "businesscore",
    Username = "businesscore", Password = bcPass,
    Options = "-c default_transaction_read_only=on"
}.ConnectionString;
var connPg = new NpgsqlConnectionStringBuilder
{
    Host = "localhost", Port = 5433, Database = "rag",
    Username = "postgres", Password = pgPass
}.ConnectionString;

var env = new Entorno
{
    ConnStrBc = connBc,
    ConnStrPg = connPg,
    ModelChat = "llama3.2:3b",
    ModelEmbed = "nomic-embed-text",
    RaizP8 = raizP8 ?? "",
    RaizP9 = raizP9,
    HttpFactory = url => new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) }
};

var escenario = new Escenario("base", env.ModelChat);
try
{
    switch (modo)
    {
        case "comparar":
            Console.WriteLine($"  {(parcial ? "corrida parcial" : "corrida completa")} · escritura: {salidaBase}*");
            await Comparativa.CorrerTodo(casos, env, salidaBase);
            break;
        case "explorar":
            await Explorador.RunAsync(env);
            break;
        case "casos":
            ListarCasos(casos);
            break;
        case "bateria":
            Console.WriteLine($"  {(parcial ? "corrida parcial" : "corrida completa")} · escritura: {salidaBase}*");
            var r = await Runner.CorrerAsync(casos, escenario, env, salidaBase);
            Console.WriteLine($"\nResumen: {r.Pass}/{r.Total} ({r.Tasa:P0})");
            break;
        default:
            // Cinturón y tirantes: la validación de arriba ya cortó los modos
            // desconocidos, pero si algo se colara, se muestra la ayuda en vez
            // de correr los 36 casos en silencio.
            Ayuda(modo, flagsDesconocidos);
            break;
    }
}
catch (FidelidadDelPromptException ex)
{
    Console.WriteLine($"[error] {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"[error] No pudo correr: {ex.Message}");
    return 1;
}
return 0;

// La ayuda: los modos válidos y cómo usarlos. Se muestra cuando el modo o
// un flag no se entienden (y en vez de correr la batería a ciegas).
static void Ayuda(string modo, IReadOnlyList<string> flagsDesconocidos)
{
    if (modo is not ("bateria" or "comparar" or "explorar" or "casos"))
        Console.WriteLine($"[error] Modo desconocido: \"{modo}\".");
    foreach (var f in flagsDesconocidos)
        Console.WriteLine($"[error] Flag desconocido: \"{f}\" (los modos no llevan guiones: \"{f.TrimStart('-')}\").");

    Console.WriteLine("""

    === Modos ===
      bateria    la batería: 36 casos, un escenario → resultados-base.csv/.json
      comparar   los 7 escenarios de la comparativa → resultados-<escenario>.csv/.json
      explorar   el chat de diagnóstico: pregunta → embedding, top-k, respuesta y veredicto
      casos      lista la batería (id, grupo, tenant, pregunta, expectativa, nota) sin correr nada
      (sin modo) lo mismo que "bateria"

    === Flags ===
      --solo=<grupo>  sólo ese grupo (R, A, I, G, B, D o el nombre completo) → resultados-parcial-*
      --rapido        el subconjunto rápido (29 casos) → resultados-parcial-*

    === Ejemplos ===
      dotnet run                     # la batería
      dotnet run -- comparar         # la comparativa
      dotnet run -- --solo=Deriva    # sólo el grupo D
      dotnet run -- casos --solo=A   # lista el grupo A, sin correr nada
      printf '¿cuánto sale Panchos?\n/salir\n' | dotnet run -- explorar
    """);
}

// La lista de la batería: id, grupo, tenant, pregunta, expectativa y nota.
// No corre nada (ni la DB): es para ver qué se va a medir.
static void ListarCasos(IReadOnlyList<Caso> casos)
{
    Console.WriteLine($"\n=== La batería: {casos.Count} casos ===");
    foreach (var c in casos)
    {
        Console.WriteLine($"  {c.Id,-4} {c.Grupo,-12} {c.Tenant,-18} {c.Pregunta}");
        var expectativas = new List<string>();
        if (c.EsperaTexto is not null) expectativas.Add($"espera \"{c.EsperaTexto}\" (K={c.KValido})");
        if (c.ExigeNoInfo) expectativas.Add("rechazo esperado");
        if (c.ExigeSinSystemPrompt) expectativas.Add("sin system prompt");
        if (c.ExigeError is not null) expectativas.Add($"error esperado: {c.ExigeError}");
        if (c.ProhibidoEn.Length > 0) expectativas.Add($"prohíbe: {string.Join(", ", c.ProhibidoEn)}");
        foreach (var e in expectativas) Console.WriteLine($"       {e}");
        if (!string.IsNullOrWhiteSpace(c.Nota)) Console.WriteLine($"       nota: {c.Nota}");
    }
}

// Sin shell de por medio: parsea KEY=VALUE plano (el password tiene ;&~ y
// cualquier quoting/expansión de shell lo rompería). Busca ./.env hacia arriba
// (cubre F5 desde bin/).
static string? LeerSecret(string clave)
{
    var deEnv = Environment.GetEnvironmentVariable(clave);
    if (!string.IsNullOrWhiteSpace(deEnv)) return deEnv;
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, ".env");
        if (!File.Exists(path)) continue;
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (!t.StartsWith(clave + "=", StringComparison.Ordinal)) continue;
            var v = t[(clave.Length + 1)..].Trim();
            if (v.Length > 1 && v.StartsWith('"') && v.EndsWith('"')) v = v[1..^1];
            return v;
        }
    }
    return null;
}
