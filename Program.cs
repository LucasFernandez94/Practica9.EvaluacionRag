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

var raizP8 = Deriva.RaizP8Desde(raizP9);
if (raizP8 is null)
{
    Console.WriteLine("[skip] No encontré ../Practica8.RagPostgres: los casos de deriva se marcan SKIPPED.");
    return 0;
}

Console.WriteLine($"\n--- Check de deriva contra {raizP8} ---");
var drift = Deriva.Verificar(raizP8, raizP9);
foreach (var d in drift)
    Console.WriteLine($"  {(d.Igual ? "✔" : "✘")} {d.Archivo}: {d.Detalle}");

var ok = drift.All(d => d.Igual);
Console.WriteLine($"\n  → {drift.Count(d => d.Igual)}/{drift.Count} archivos sin deriva.");
return ok ? 0 : 1;
