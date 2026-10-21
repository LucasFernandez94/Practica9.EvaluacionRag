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
    var resumen = await Runner.CorrerAsync(Casos.Todos, escenario, env, "resultados");
    Console.WriteLine($"\nResumen: {resumen.Pass}/{resumen.Total} ({resumen.Tasa:P0})");
}
catch (FidelidadDelPromptException ex)
{
    Console.WriteLine($"[error] {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine($"[error] La batería no pudo correr: {ex.Message}");
    return 1;
}
return 0;

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
